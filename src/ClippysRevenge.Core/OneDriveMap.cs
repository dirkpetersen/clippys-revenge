using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>SPEC 26.3. UrlPrefix is an absolute https URL ending in "/"; MountPoint a local folder.</summary>
public sealed record SyncedRoot(string UrlPrefix, string MountPoint, string LibraryType, bool IsFolderScope);

public abstract record MappedLink
{
    private MappedLink() { }

    /// <summary>Path is MountPoint + the remaining segments; Root is the matched scope (its MountPoint is the
    /// only PathPolicy root for this path). PathPolicy has NOT run yet.</summary>
    public sealed record Local(string Path, SyncedRoot Root) : MappedLink;

    public sealed record Unmappable(string Reason) : MappedLink;
}

/// <summary>
/// Maps a OneDrive/SharePoint link to the local copy the OneDrive sync client keeps (SPEC 26.3).
/// Pure: no network, no registry, no logging.
/// </summary>
public static partial class OneDriveMap
{
    /// <summary>Same text as AttachmentReasons.NotSynced (M19.2).</summary>
    public const string NotSyncedReason = AttachmentReasons.NotSynced;

    private static readonly MappedLink.Unmappable NotSynced = new(NotSyncedReason);

    [GeneratedRegex(@"^:[a-z]{1,2}:$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShareTypeSegment();

    /// <summary>SPEC 26.3 "Mapping" steps 1-4. Never throws (null/garbage gives Unmappable).</summary>
    public static MappedLink Map(string? url, IReadOnlyList<SyncedRoot>? roots)
    {
        try
        {
            return MapCore(url, roots);
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException or InvalidOperationException)
        {
            return NotSynced;
        }
    }

    private static MappedLink MapCore(string? url, IReadOnlyList<SyncedRoot>? roots)
    {
        if (string.IsNullOrWhiteSpace(url) || roots is null) return NotSynced;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !OneDriveLinks.IsMicrosoftFileHost(uri))
            return NotSynced;

        var segs = DirectSegments(uri);
        if (segs is null) return NotSynced;
        var host = OneDriveLinks.NormalizedHost(uri);

        SyncedRoot? best = null;
        var bestCount = -1;
        foreach (var root in roots)
        {
            if (root is null || string.IsNullOrWhiteSpace(root.MountPoint)) continue;
            if (!Uri.TryCreate(root.UrlPrefix, UriKind.Absolute, out var prefix)
                || !OneDriveLinks.IsMicrosoftFileHost(prefix)) continue;
            if (OneDriveLinks.NormalizedHost(prefix) != host) continue;
            var prefixSegs = PlainSegments(prefix);
            if (prefixSegs is null || prefixSegs.Length > segs.Length) continue;

            var match = true;
            for (var i = 0; i < prefixSegs.Length; i++)
            {
                if (!prefixSegs[i].Equals(segs[i], StringComparison.OrdinalIgnoreCase)) { match = false; break; }
            }
            if (match && prefixSegs.Length > bestCount)
            {
                best = root;
                bestCount = prefixSegs.Length;
            }
        }

        if (best is null || bestCount >= segs.Length) return NotSynced;

        var parts = new string[segs.Length - bestCount + 1];
        parts[0] = best.MountPoint.Trim().TrimEnd('\\', '/');
        for (var i = bestCount; i < segs.Length; i++)
        {
            if (FileNames.SafeFileName(segs[i]) != segs[i]) return NotSynced;
            parts[i - bestCount + 1] = segs[i];
        }
        return new MappedLink.Local(Path.Join(parts), best);
    }

    /// <summary>Steps 1-2 for an already parsed https URL on a Microsoft host: the decoded path segments of a
    /// direct path (share-link "/:x:/r/" prefix removed), or null when unmappable.</summary>
    internal static string[]? DirectSegments(Uri uri)
    {
        var host = OneDriveLinks.NormalizedHost(uri);
        if (host is "onedrive.live.com" or "1drv.ms") return null;

        var segs = PlainSegments(uri);
        if (segs is null) return null;
        foreach (var s in segs)
            if (s.Equals("_layouts", StringComparison.OrdinalIgnoreCase)) return null;

        if (segs.Length > 0 && ShareTypeSegment().IsMatch(segs[0]))
        {
            if (segs.Length > 1 && segs[1].Equals("r", StringComparison.OrdinalIgnoreCase)) return segs[2..];
            return null;
        }
        return segs;
    }

    /// <summary>Decoded, non-empty path segments; null for a backslash, control character or "."/".." segment.</summary>
    private static string[]? PlainSegments(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        foreach (var c in path)
            if (c == '\\' || char.IsControl(c)) return null;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segs)
            if (s is "." or "..") return null;
        return segs;
    }
}

public abstract record SyncedFileResult
{
    private SyncedFileResult() { }

    /// <summary>FileName = Path.GetFileName(full).</summary>
    public sealed record Bytes(byte[] Data, string FileName) : SyncedFileResult;

    /// <summary>Missing, folder, or a refused path.</summary>
    public sealed record NotSynced : SyncedFileResult;

    public sealed record TooLarge(long Length) : SyncedFileResult;

    /// <summary>Exception type name only.</summary>
    public sealed record Failed(string ErrorType) : SyncedFileResult;
}

public static class SyncedFileReader
{
    private const int ChunkSize = 81_920;

    /// <summary>SPEC 26.3 step 5 and "Reading a synced file". Synchronous (the caller runs it on the thread
    /// pool with its timeout). Throws only OperationCanceledException and ArgumentNullException.</summary>
    public static SyncedFileResult Read(MappedLink.Local link, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        ct.ThrowIfCancellationRequested();

        try
        {
            var full = PathPolicy.Check(link.Path, [link.Root.MountPoint], PathMode.Read);

            var len = new FileInfo(full).Length;
            if (len > maxBytes) return new SyncedFileResult.TooLarge(len);

            var buf = new byte[(int)Math.Min(len, 1L << 30)];
            var total = 0;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.SequentialScan))
            {
                var probe = new byte[1];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int n;
                    if (total == buf.Length)
                    {
                        n = fs.Read(probe, 0, 1);
                        if (n == 0) break;
                        var grown = (int)Math.Min(Math.Max((long)buf.Length * 2, (long)total + ChunkSize), Array.MaxLength);
                        if (grown <= total) return new SyncedFileResult.TooLarge(total + 1L);
                        Array.Resize(ref buf, grown);
                        buf[total++] = probe[0];
                    }
                    else
                    {
                        n = fs.Read(buf, total, Math.Min(ChunkSize, buf.Length - total));
                        if (n == 0) break;
                        total += n;
                    }
                    if (total > maxBytes) return new SyncedFileResult.TooLarge(total);
                }
            }

            if (total != buf.Length) Array.Resize(ref buf, total);
            return new SyncedFileResult.Bytes(buf, Path.GetFileName(full));
        }
        catch (Exception e) when (e is PathNotAllowedException or FileNotFoundException or DirectoryNotFoundException
                                      or ArgumentException)
        {
            return new SyncedFileResult.NotSynced();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SyncedFileResult.Failed(e.GetType().Name);
        }
    }
}
