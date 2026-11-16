using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>%LOCALAPPDATA%\ClippysRevenge\ai-files (SPEC 26.9, 5.1). Mirrors LinkPaths.</summary>
public sealed partial record AiFilePaths(string Folder)
{
    public const string FolderName = "ai-files";

    /// <exception cref="ArgumentException">localAppData blank or not fully qualified.</exception>
    public static AiFilePaths Default(string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new ArgumentException("localAppData must be a fully qualified path.", nameof(localAppData));
        return new AiFilePaths(Path.Combine(localAppData, "ClippysRevenge", FolderName));
    }

    /// <summary>32 lower-case hex chars from 16 random bytes (RandomNumberGenerator) + ".msg" when item, else ".bin".</summary>
    public static string NewFileName(bool item) =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + (item ? ".msg" : ".bin");

    /// <summary>^[0-9a-f]{32}\.(bin|msg)$, ordinal (lower case only). Null = false.</summary>
    public static bool IsTempFileName(string? name) => name is not null && TempName().IsMatch(name);

    [GeneratedRegex(@"\A[0-9a-f]{32}\.(bin|msg)\z", RegexOptions.CultureInvariant)]
    private static partial Regex TempName();
}

public sealed class AiFilesOptions
{
    /// <summary>Fully qualified (AiFilePaths.Default(...).Folder in the app).</summary>
    public required string Folder { get; init; }

    /// <summary>App: PrivateFolder.Ensure. Throws on any failure. Tests: d => Directory.CreateDirectory(d).</summary>
    public required Action<string> EnsurePrivateFolder { get; init; }

    public ILinkFileSystem Files { get; init; } = LinkFileSystem.Instance;   // the M17 seam, reused
    public ILogger? Logger { get; init; }
}

/// <summary>The ai-files folder can't be used (Ensure failed, junction/symlink, PathPolicy refused).
/// Message is constant and never holds the path.</summary>
public sealed class AiFolderUnusableException : IOException
{
    public const string DefaultMessage = "Clippy's private folder for attachment text can't be used.";

    public AiFolderUnusableException() : base(DefaultMessage)
    {
    }

    public AiFolderUnusableException(Exception? inner) : base(DefaultMessage, inner)
    {
    }
}

/// <summary>Temp copies of Outlook attachments for AI drafts (SPEC 26.9). Thread-safe (one lock).
/// Logs only constant texts, counts and exception type names.</summary>
public sealed class AiFiles
{
    private readonly AiFilesOptions _options;
    private readonly ILinkFileSystem _files;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _retry = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="ArgumentNullException">options, Folder or EnsurePrivateFolder null.</exception>
    /// <exception cref="ArgumentException">Folder not fully qualified.</exception>
    public AiFiles(AiFilesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Folder);
        ArgumentNullException.ThrowIfNull(options.EnsurePrivateFolder);
        ArgumentNullException.ThrowIfNull(options.Files);
        if (string.IsNullOrWhiteSpace(options.Folder) || !Path.IsPathFullyQualified(options.Folder))
            throw new ArgumentException("Folder must be a fully qualified path.", nameof(options));

        _options = options;
        _files = options.Files;
        _log = options.Logger;
        Folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Folder));
    }

    /// <summary>The folder, Path.GetFullPath'd, without a trailing separator.</summary>
    public string Folder { get; }

    /// <summary>Test hook: failed deletes waiting for a retry.</summary>
    internal int PendingRetries
    {
        get
        {
            lock (_gate) return _retry.Count;
        }
    }

    /// <summary>Retries pending deletes, ensures the folder, refuses a link, PathPolicy-checks the folder,
    /// returns Folder + NewFileName(item). Does not create the file.</summary>
    /// <exception cref="AiFolderUnusableException">any of the folder steps failed (inner = the cause).</exception>
    public string PrepareTempPath(bool item)
    {
        lock (_gate)
        {
            foreach (var path in _retry.ToArray())
            {
                try
                {
                    _files.DeleteFile(path);
                    _retry.Remove(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Kept for the next try; the Warning was logged when it first failed.
                }
            }

            try
            {
                _options.EnsurePrivateFolder(Folder);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AiFolderUnusableException(ex);
            }

            bool isLink;
            try
            {
                isLink = _files.IsLink(Folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AiFolderUnusableException(ex);
            }
            if (isLink) throw new AiFolderUnusableException();

            string checkedFolder;
            try
            {
                checkedFolder = PathPolicy.Check(Folder, [Folder], PathMode.Write);
            }
            catch (Exception ex) when (ex is PathNotAllowedException or IOException or UnauthorizedAccessException)
            {
                throw new AiFolderUnusableException(ex);
            }

            return Path.Combine(checkedFolder, AiFilePaths.NewFileName(item));
        }
    }

    /// <summary>Deletes a file from PrepareTempPath. Never throws for IO: a failed delete is remembered and
    /// retried at the next PrepareTempPath; one Warning with the exception type.</summary>
    /// <exception cref="ArgumentNullException">path null.</exception>
    /// <exception cref="ArgumentException">path not directly in Folder or name not IsTempFileName (programming error).</exception>
    public void Discard(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        const string message = "path must be an AI temp file.";
        if (!AiFilePaths.IsTempFileName(Path.GetFileName(path))) throw new ArgumentException(message, nameof(path));
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(message, nameof(path));
        }
        if (dir is null || !string.Equals(Path.TrimEndingDirectorySeparator(dir), Folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(message, nameof(path));

        lock (_gate) TryDelete(path, warn: true);
    }

    /// <summary>App start: ensure the folder, then delete every temp-named file directly in it. Never throws.</summary>
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
                _log?.LogWarning("AI file folder not usable at start-up ({Type}); cleanup skipped.", ex.GetType().Name);
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
                _log?.LogWarning("AI file folder is a junction or symbolic link; cleanup skipped.");
                return;
            }

            var count = 0;
            try
            {
                foreach (var path in _files.EnumerateFiles(Folder).ToList())
                {
                    if (!AiFilePaths.IsTempFileName(Path.GetFileName(path))) continue;
                    lock (_gate) TryDelete(path, warn: true);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning("AI file cleanup failed ({Type}).", ex.GetType().Name);
            }
            if (count > 0) _log?.LogDebug("AI file folder cleaned ({Count} files).", count);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI file cleanup failed ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Call under the gate. A failure is remembered and retried before the next save. Never logs the path.</summary>
    private void TryDelete(string path, bool warn)
    {
        try
        {
            _files.DeleteFile(path);
            _retry.Remove(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _retry.Add(path);
            if (warn)
                _log?.LogWarning("Could not delete an AI temp file ({Type}); retrying before the next save.", ex.GetType().Name);
        }
    }
}
