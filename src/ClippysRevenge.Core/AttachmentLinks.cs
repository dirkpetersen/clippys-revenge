using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>File operations on the link folder, injectable for tests (SPEC 24.2).</summary>
public interface ILinkFileSystem
{
    /// <summary>True when the folder exists and is a junction or symbolic link (DirectoryInfo.LinkTarget != null).
    /// False when it doesn't exist. May throw IOException/UnauthorizedAccessException.</summary>
    bool IsLink(string folder);

    /// <summary>Full paths of the files directly in the folder (no recursion). Empty when the folder is missing.
    /// May throw IOException/UnauthorizedAccessException.</summary>
    IEnumerable<string> EnumerateFiles(string folder);

    /// <summary>Deletes the file. A missing file or folder is not an error.
    /// Throws IOException/UnauthorizedAccessException when it can't be deleted.</summary>
    void DeleteFile(string path);
}

/// <summary>The real file system. Delete = File.Delete, swallowing DirectoryNotFoundException.</summary>
public sealed class LinkFileSystem : ILinkFileSystem
{
    public static readonly LinkFileSystem Instance = new();

    public bool IsLink(string folder) =>
        Directory.Exists(folder) && new DirectoryInfo(folder).LinkTarget is not null;

    public IEnumerable<string> EnumerateFiles(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : [];

    public void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public sealed class AttachmentLinksOptions
{
    /// <summary>The link folder, fully qualified (LinkPaths.Default(...).Folder in the app).</summary>
    public required string Folder { get; init; }

    /// <summary>Creates the folder with the user-only ACL and refuses a link (App: PrivateFolder.Ensure, M17.3).
    /// Throws on any failure. Tests pass d => Directory.CreateDirectory(d).</summary>
    public required Action<string> EnsurePrivateFolder { get; init; }

    public ILinkFileSystem Files { get; init; } = LinkFileSystem.Instance;
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public ILogger? Logger { get; init; }
}

/// <summary>One open link. ToString() never shows the token or the path.</summary>
public sealed record AttachmentLink(string Token, string TempPath, string FileName, long Size,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(AttachmentLink);
}

public enum LinkRefusal { TooBig, TooManyOpen, TooManyBytes, RateLimited, FolderUnusable }

/// <summary>A refusal the model can act on (message = the pinned SPEC 24.2/24.3 text).</summary>
public sealed class AttachmentLinkException : McpException
{
    public AttachmentLinkException(LinkRefusal reason, string message, Exception? inner = null)
        : base(message, inner)
    {
        Reason = reason;
    }

    public LinkRefusal Reason { get; }
}

/// <summary>In-memory one-time download links (SPEC 24.3). Thread-safe. Memory only: nothing on disk but the temp files.</summary>
public sealed class AttachmentLinks : IDisposable
{
    public const string FilesPath = "/files";
    public const int TokenLength = 43;
    public const long MaxLinkBytes = 100L * 1024 * 1024;
    public const int MaxOpenLinks = 10;
    public const long MaxOpenBytes = 200L * 1024 * 1024;
    public const int MaxLinksPerWindow = 20;
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    private readonly AttachmentLinksOptions _options;
    private readonly ILinkFileSystem _files;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly ConcurrentDictionary<string, AttachmentLink> _links = new(StringComparer.Ordinal);
    // Consumed links whose file is still being streamed: counted until Complete.
    private readonly ConcurrentDictionary<string, AttachmentLink> _streaming = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _rate = new();
    private readonly HashSet<string> _retry = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITimer _timer;
    private int _disposed;

    /// <exception cref="ArgumentNullException">options, Folder, EnsurePrivateFolder, Files or Time is null.</exception>
    /// <exception cref="ArgumentException">Folder is blank or not fully qualified.</exception>
    public AttachmentLinks(AttachmentLinksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Folder);
        ArgumentNullException.ThrowIfNull(options.EnsurePrivateFolder);
        ArgumentNullException.ThrowIfNull(options.Files);
        ArgumentNullException.ThrowIfNull(options.Time);
        if (string.IsNullOrWhiteSpace(options.Folder) || !Path.IsPathFullyQualified(options.Folder))
            throw new ArgumentException("Folder must be a fully qualified path.", nameof(options));

        _options = options;
        _files = options.Files;
        _time = options.Time;
        _log = options.Logger;
        Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Folder));
        _timer = _time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public string Folder { get; }

    /// <summary>Unexpired links in the store plus downloads still streaming.</summary>
    public int OpenCount
    {
        get
        {
            var now = _time.GetUtcNow();
            return _links.Values.Count(l => now < l.ExpiresAt) + _streaming.Count;
        }
    }

    /// <summary>Sum of the sizes of the unexpired links and the downloads still streaming.</summary>
    public long OpenBytes
    {
        get
        {
            var now = _time.GetUtcNow();
            return _links.Values.Where(l => now < l.ExpiresAt).Sum(l => l.Size) + _streaming.Values.Sum(l => l.Size);
        }
    }

    /// <summary>32 random bytes, base64url without padding: 43 chars of [A-Za-z0-9_-].</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>43 chars of [A-Za-z0-9_-] (ordinal). Null/empty = false.</summary>
    public static bool IsWellFormedToken(string? token)
    {
        if (token is null || token.Length != TokenLength) return false;
        foreach (var c in token)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Pre-check before the COM save: open count, open bytes, rate (in that order).</summary>
    public void CheckCanCreate()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PurgeExpired(now);
            PruneRate(now);
            if (OpenCount >= MaxOpenLinks)
                throw new AttachmentLinkException(LinkRefusal.TooManyOpen, LinkTexts.TooManyOpen);
            if (OpenBytes >= MaxOpenBytes)
                throw new AttachmentLinkException(LinkRefusal.TooManyBytes, LinkTexts.TooManyBytes);
            CheckRate(now);
        }
    }

    /// <summary>Ensures the private folder and returns a fresh temp path in it.</summary>
    public string PrepareTempPath()
    {
        ThrowIfDisposed();
        try
        {
            _options.EnsurePrivateFolder(Folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AttachmentLinkException(LinkRefusal.FolderUnusable, LinkTexts.FolderUnusable, ex);
        }

        try
        {
            if (_files.IsLink(Folder))
                throw new AttachmentLinkException(LinkRefusal.FolderUnusable, LinkTexts.FolderUnusable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AttachmentLinkException(LinkRefusal.FolderUnusable, LinkTexts.FolderUnusable, ex);
        }

        string checkedFolder;
        try
        {
            checkedFolder = PathPolicy.Check(Folder, [Folder], PathMode.Write);
        }
        catch (Exception ex) when (ex is PathNotAllowedException or IOException or UnauthorizedAccessException)
        {
            throw new AttachmentLinkException(LinkRefusal.FolderUnusable, LinkTexts.FolderUnusable, ex);
        }

        return Path.Combine(checkedFolder, LinkPaths.NewFileName());
    }

    /// <summary>Registers a saved temp file. Rechecks every limit atomically; on refusal deletes the file, then throws.</summary>
    public AttachmentLink Add(string tempPath, string fileName, long size)
    {
        ArgumentNullException.ThrowIfNull(tempPath);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ValidateTempPath(tempPath);

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                TryDelete(tempPath);
                throw new ObjectDisposedException(GetType().FullName);
            }
            var now = _time.GetUtcNow();
            PurgeExpired(now);
            PruneRate(now);
            try
            {
                if (size > MaxLinkBytes)
                    throw new AttachmentLinkException(LinkRefusal.TooBig, LinkTexts.TooBig(size));
                if (OpenCount >= MaxOpenLinks)
                    throw new AttachmentLinkException(LinkRefusal.TooManyOpen, LinkTexts.TooManyOpen);
                if (OpenBytes + size > MaxOpenBytes)
                    throw new AttachmentLinkException(LinkRefusal.TooManyBytes, LinkTexts.TooManyBytes);
                CheckRate(now);
            }
            catch (AttachmentLinkException)
            {
                TryDelete(tempPath);
                throw;
            }

            while (true)
            {
                var link = new AttachmentLink(NewToken(), tempPath, fileName, size, now, now + LinkLifetime);
                if (!_links.TryAdd(link.Token, link)) continue;
                _rate.Enqueue(now);
                return link;
            }
        }
    }

    /// <summary>Single use: atomically removes and returns the link, or null when the token is malformed, unknown,
    /// already used or expired (an expired one's file is deleted). Never throws; null after Dispose.</summary>
    public AttachmentLink? TryConsume(string? token)
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        if (!IsWellFormedToken(token)) return null;
        lock (_gate)
        {
            if (!_links.TryRemove(token!, out var link)) return null;
            if (_time.GetUtcNow() >= link.ExpiresAt)
            {
                TryDelete(link.TempPath);
                return null;
            }
            _streaming[link.Token] = link;
            return link;
        }
    }

    /// <summary>After a download ends (or aborts): stops counting it and deletes the link's temp file; a failed delete is retried at the next sweep.</summary>
    public void Complete(AttachmentLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_gate)
        {
            _streaming.TryRemove(link.Token, out _);
            TryDelete(link.TempPath);
        }
    }

    /// <summary>Deletes a temp file from PrepareTempPath that never became a link (failed save).</summary>
    public void Discard(string tempPath)
    {
        ArgumentNullException.ThrowIfNull(tempPath);
        ValidateTempPath(tempPath);
        TryDelete(tempPath);
    }

    /// <summary>Removes expired links, deletes their files, retries failed deletes, prunes the rate history. Never throws.</summary>
    public void Sweep()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                foreach (var path in _retry.ToArray()) TryDelete(path);
                var expired = PurgeExpired(now);
                PruneRate(now);
                if (expired > 0)
                    _log?.LogDebug("Download links: {Expired} expired, {Open} open.", expired, _links.Count);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Download link sweep failed ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Port change, host restart, app exit: removes every link and deletes its file. Never throws.</summary>
    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                foreach (var path in _retry.ToArray()) TryDelete(path);
                var count = 0;
                foreach (var key in _links.Keys.ToArray())
                {
                    if (!_links.TryRemove(key, out var link)) continue;
                    count++;
                    TryDelete(link.TempPath);
                }
                if (count > 0) _log?.LogDebug("Download links cleared ({Count}).", count);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Download link sweep failed ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>App start: ensure the folder, then delete every temp-file-named file directly in it.</summary>
    public void CleanUpAtStart()
    {
        try
        {
            try
            {
                _options.EnsurePrivateFolder(Folder);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Download link folder not usable at start-up ({Type}); cleanup skipped.", ex.GetType().Name);
                return;
            }

            bool isLink;
            try
            {
                isLink = _files.IsLink(Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                isLink = true;
            }
            if (isLink)
            {
                _log?.LogWarning("Download link folder is a junction or symbolic link; cleanup skipped.");
                return;
            }

            var count = 0;
            try
            {
                foreach (var path in _files.EnumerateFiles(Folder))
                {
                    if (!LinkPaths.IsTempFileName(Path.GetFileName(path))) continue;
                    TryDelete(path);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning("Download link cleanup failed ({Type}).", ex.GetType().Name);
            }
            if (count > 0) _log?.LogDebug("Download link folder cleaned ({Count} files).", count);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Download link cleanup failed ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Stops the timer, then Clear(). Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer.Dispose();
        Clear();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ValidateTempPath(string tempPath)
    {
        const string message = "tempPath must be a link temp file in the link folder.";
        if (!LinkPaths.IsTempFileName(Path.GetFileName(tempPath)))
            throw new ArgumentException(message, nameof(tempPath));
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(tempPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(message, nameof(tempPath));
        }
        if (dir is null || !string.Equals(Path.TrimEndingDirectorySeparator(dir), Folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(message, nameof(tempPath));
    }

    private void PruneRate(DateTimeOffset now)
    {
        while (_rate.Count > 0 && _rate.Peek() <= now - RateWindow) _rate.Dequeue();
    }

    private void CheckRate(DateTimeOffset now)
    {
        if (_rate.Count < MaxLinksPerWindow) return;
        var wait = _rate.Peek() + RateWindow - now;
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        throw new AttachmentLinkException(LinkRefusal.RateLimited, LinkTexts.RateLimited(minutes));
    }

    /// <summary>Removes every expired link (now >= ExpiresAt) and deletes its file. Call under the gate.</summary>
    private int PurgeExpired(DateTimeOffset now)
    {
        var expired = 0;
        foreach (var l in _links.Values.ToArray())
        {
            if (now < l.ExpiresAt) continue;
            if (!_links.TryRemove(l.Token, out var removed)) continue;
            expired++;
            TryDelete(removed.TempPath);
        }
        return expired;
    }

    /// <summary>Deletes a file. A failure is remembered and retried at the next sweep. Never logs the path.</summary>
    private void TryDelete(string path)
    {
        lock (_gate)
        {
            try
            {
                _files.DeleteFile(path);
                _retry.Remove(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _retry.Add(path);
                _log?.LogWarning("Could not delete a download link file ({Type}); retrying at the next sweep.", ex.GetType().Name);
            }
        }
    }
}
