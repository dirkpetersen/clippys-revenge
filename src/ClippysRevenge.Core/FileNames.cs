namespace ClippysRevenge.Core;

public static class FileNames
{
    public const string Fallback = "attachment";
    public const int MaxLength = 200;

    private const string Invalid = "\\/:*?\"<>|";

    private static readonly HashSet<string> DeviceNames = BuildDeviceNames();

    private static HashSet<string> BuildDeviceNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var i = 0; i <= 9; i++)
        {
            set.Add("COM" + i);
            set.Add("LPT" + i);
        }
        return set;
    }

    /// <summary>Never throws. null gives "attachment".</summary>
    public static string SafeFileName(string? name)
    {
        if (name is null) return Fallback;

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Invalid.Contains(chars[i]) || char.IsControl(chars[i])) chars[i] = '_';

        var s = TrimEnd(new string(chars).TrimStart());
        if (s.Length == 0) return Fallback;

        var dot = s.IndexOf('.');
        var head = (dot < 0 ? s : s[..dot]).TrimEnd();
        if (DeviceNames.Contains(head)) s = "_" + s;

        if (s.Length > MaxLength)
        {
            var ext = Path.GetExtension(s);
            string cut;
            if (ext.Length is >= 1 and <= 16)
            {
                var keep = s[..(MaxLength - ext.Length)];
                if (keep.Length > 0 && char.IsHighSurrogate(keep[^1])) keep = keep[..^1];
                cut = keep + ext;
            }
            else
            {
                cut = s[..MaxLength];
                if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
            }
            s = TrimEnd(cut);
        }
        return s;
    }

    private static string TrimEnd(string s)
    {
        var end = s.Length;
        while (end > 0 && (s[end - 1] == '.' || char.IsWhiteSpace(s[end - 1]))) end--;
        return s[..end];
    }

    public static string UniquePath(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("Directory is required.", nameof(dir));

        var safe = SafeFileName(name);
        var candidate = Path.Combine(dir, safe);
        if (!Exists(candidate)) return candidate;

        var ext = Path.GetExtension(safe);
        var stem = Path.GetFileNameWithoutExtension(safe);
        if (stem.Length == 0)
        {
            stem = safe;
            ext = "";
        }
        for (var n = 2; n <= 9999; n++)
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!Exists(candidate)) return candidate;
        }
        throw new IOException($"No free file name for \"{safe}\" in \"{dir}\".");
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
