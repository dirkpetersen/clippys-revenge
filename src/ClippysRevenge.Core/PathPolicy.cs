namespace ClippysRevenge.Core;

public enum PathMode { Read, Write }

public enum PathRejectReason
{
    Empty,
    InvalidCharacters,
    DeviceOrUnc,
    AlternateDataStream,
    NotFullyQualified,
    ReservedDeviceName,
    NoAllowedFolders,
    OutsideAllowedFolders,
    LinkBelowRoot,
    Inaccessible,
}

public sealed class PathNotAllowedException : Exception
{
    public PathNotAllowedException(string? path, PathRejectReason reason, IReadOnlyList<string> allowedRoots)
        : base($"Path '{path}' is not allowed: {PathPolicy.Describe(reason)}. Allowed folders: {(allowedRoots.Count == 0 ? "(none)" : string.Join("; ", allowedRoots))}.")
    {
        RequestedPath = path;
        Reason = reason;
        AllowedRoots = allowedRoots;
    }

    /// <summary>The raw input, as given.</summary>
    public string? RequestedPath { get; }
    public PathRejectReason Reason { get; }
    /// <summary>Normalized roots.</summary>
    public IReadOnlyList<string> AllowedRoots { get; }
}

/// <summary>
/// The single gate every tool file path goes through (SPEC section 11 and 12).
/// Known limitation: there is a time-of-check/time-of-use gap between the link
/// walk and the caller's file operation.
/// </summary>
public static class PathPolicy
{
    /// <summary>
    /// Returns the normalized full path. Throws <see cref="PathNotAllowedException"/> on policy refusal,
    /// <see cref="FileNotFoundException"/> (Read: missing or a folder), <see cref="IOException"/>
    /// (Write: path is a file, or CreateDirectory fails), <see cref="ArgumentNullException"/> and
    /// <see cref="ArgumentException"/> (relativeTo not fully qualified).
    /// </summary>
    public static string Check(string? path, IReadOnlyList<string> allowedRoots, PathMode mode, string? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        var roots = NormalizeRoots(allowedRoots);

        PathNotAllowedException Refuse(PathRejectReason r) => new(path, r, roots);

        if (string.IsNullOrWhiteSpace(path)) throw Refuse(PathRejectReason.Empty);
        foreach (var c in path)
            if (c < 0x20) throw Refuse(PathRejectReason.InvalidCharacters);
        if (IsDeviceOrUnc(path)) throw Refuse(PathRejectReason.DeviceOrUnc);
        if (HasAlternateStream(path)) throw Refuse(PathRejectReason.AlternateDataStream);

        string full;
        if (Path.IsPathFullyQualified(path))
        {
            full = Path.GetFullPath(path);
        }
        else if (Path.IsPathRooted(path))
        {
            throw Refuse(PathRejectReason.NotFullyQualified);
        }
        else
        {
            if (relativeTo is null) throw Refuse(PathRejectReason.NotFullyQualified);
            if (!Path.IsPathFullyQualified(relativeTo))
                throw new ArgumentException("relativeTo must be a fully qualified path.", nameof(relativeTo));
            full = Path.GetFullPath(path, relativeTo);
        }

        if (IsDeviceOrUnc(full)) throw Refuse(PathRejectReason.DeviceOrUnc);
        if (HasAlternateStream(full)) throw Refuse(PathRejectReason.AlternateDataStream);
        full = TrimTrailing(full);

        var fullRoot = Path.GetPathRoot(full) ?? "";
        foreach (var comp in full[fullRoot.Length..].Split(Seps, StringSplitOptions.RemoveEmptyEntries))
            if (IsReserved(comp)) throw Refuse(PathRejectReason.ReservedDeviceName);

        if (roots.Count == 0) throw Refuse(PathRejectReason.NoAllowedFolders);

        string? matched = null;
        foreach (var root in roots)
        {
            if (!Matches(full, root)) continue;
            if (matched is null || root.Length > matched.Length) matched = root;
        }
        if (matched is null) throw Refuse(PathRejectReason.OutsideAllowedFolders);

        if (full.Length > matched.Length)
        {
            var current = matched;
            foreach (var comp in full[matched.Length..].Split(Seps, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, comp);
                try
                {
                    FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                    if (info.LinkTarget is not null) throw Refuse(PathRejectReason.LinkBelowRoot);
                    if (!info.Exists) break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw Refuse(PathRejectReason.Inaccessible);
                }
            }
        }

        if (mode == PathMode.Read)
        {
            if (Directory.Exists(full))
                throw new FileNotFoundException($"'{full}' is a folder, not a file.", full);
            if (!File.Exists(full))
                throw new FileNotFoundException($"File not found: '{full}'.", full);
            return full;
        }

        if (File.Exists(full)) throw new IOException($"'{full}' is a file, not a folder.");
        Directory.CreateDirectory(full);
        return full;
    }

    public static string Describe(PathRejectReason reason) => reason switch
    {
        PathRejectReason.Empty => "the path is empty",
        PathRejectReason.InvalidCharacters => "the path contains control characters",
        PathRejectReason.DeviceOrUnc => "device and network (UNC) paths are not supported",
        PathRejectReason.AlternateDataStream => "alternate data streams are not supported",
        PathRejectReason.NotFullyQualified => @"the path must be absolute, like C:\Users\you\Documents\file.pdf",
        PathRejectReason.ReservedDeviceName => "the path uses a reserved device name",
        PathRejectReason.NoAllowedFolders => "no allowed folders are configured",
        PathRejectReason.OutsideAllowedFolders => "it is outside the allowed folders",
        PathRejectReason.LinkBelowRoot => "it goes through a junction or symbolic link",
        PathRejectReason.Inaccessible => "part of the path could not be inspected",
        _ => reason.ToString(),
    };

    private static readonly char[] Seps = ['\\', '/'];

    private static bool IsSep(char c) => c is '\\' or '/';

    private static bool IsDeviceOrUnc(string p) => p.Length >= 2 && IsSep(p[0]) && IsSep(p[1]);

    private static bool HasAlternateStream(string p)
    {
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] != ':') continue;
            if (i != 1) return true;
            if (!char.IsAsciiLetter(p[0])) return true;
        }
        return false;
    }

    private static string TrimTrailing(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= root.Length) return full;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    private static bool IsReserved(string component)
    {
        var dot = component.IndexOf('.');
        var stem = (dot >= 0 ? component[..dot] : component).TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '0' and <= '9';
    }

    private static List<string> NormalizeRoots(IReadOnlyList<string> allowedRoots)
    {
        var result = new List<string>();
        foreach (var r in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            if (!Path.IsPathFullyQualified(r) || IsDeviceOrUnc(r)) continue;
            string n;
            try { n = TrimTrailing(Path.GetFullPath(r)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!result.Contains(n, StringComparer.OrdinalIgnoreCase)) result.Add(n);
        }
        return result;
    }

    private static bool Matches(string full, string root)
    {
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = IsSep(root[^1]) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
