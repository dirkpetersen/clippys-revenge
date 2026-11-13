using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>SemVer 2.0.0 version (SPEC 20.2). Build metadata is accepted by TryParse and dropped.</summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public const int MaxLength = 256;

    private static readonly Regex Grammar = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex TagGrammar = new(
        @"^v(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private SemVersion(int major, int minor, int patch, string[] preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static SemVersion Zero { get; } = new(0, 0, 0, []);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>Dot-separated pre-release identifiers; empty for a full release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>Strict SemVer 2.0.0 (no trimming, no leading "v"). False for null, blank, longer than MaxLength,
    /// leading zeros in numeric parts, or numbers that don't fit an int (major/minor/patch).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
        {
            return false;
        }
        Match m;
        try
        {
            m = Grammar.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        if (!m.Success)
        {
            return false;
        }
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }
        var pre = m.Groups[4].Success ? m.Groups[4].Value.Split('.') : [];
        version = new SemVersion(major, minor, patch, pre);
        return true;
    }

    /// <exception cref="FormatException">text isn't a valid SemVer.</exception>
    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException("Not a valid SemVer 2.0.0 version.");

    /// <summary>Only "v&lt;MAJOR&gt;.&lt;MINOR&gt;.&lt;PATCH&gt;" with no pre-release and no build part.</summary>
    public static bool TryParseReleaseTag(string? tag, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (tag is null || tag.Length > 40)
        {
            return false;
        }
        Match m;
        try
        {
            m = TagGrammar.Match(tag);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        if (!m.Success)
        {
            return false;
        }
        version = new SemVersion(
            int.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            []);
        return true;
    }

    /// <summary>SemVer 2.0.0 section 11 precedence. null sorts before every version.</summary>
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }
        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }
        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }
        if (!IsPreRelease || !other.IsPreRelease)
        {
            if (IsPreRelease == other.IsPreRelease)
            {
                return 0;
            }
            return IsPreRelease ? -1 : 1;
        }
        var n = Math.Min(PreRelease.Count, other.PreRelease.Count);
        for (var i = 0; i < n; i++)
        {
            c = CompareIdentifier(PreRelease[i], other.PreRelease[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    private static bool IsNumeric(string s)
    {
        foreach (var ch in s)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }
        return s.Length > 0;
    }

    private static int CompareIdentifier(string a, string b)
    {
        var an = IsNumeric(a);
        var bn = IsNumeric(b);
        if (an && bn)
        {
            var c = a.Length.CompareTo(b.Length);
            return c != 0 ? c : Math.Sign(string.CompareOrdinal(a, b));
        }
        if (an)
        {
            return -1;
        }
        if (bn)
        {
            return 1;
        }
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Major);
        h.Add(Minor);
        h.Add(Patch);
        foreach (var id in PreRelease)
        {
            h.Add(id, StringComparer.Ordinal);
        }
        return h.ToHashCode();
    }

    /// <summary>"1.2.3" or "1.2.3-beta.1" (never a leading "v", never build metadata).</summary>
    public override string ToString()
    {
        var core = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        return IsPreRelease ? core + "-" + string.Join('.', PreRelease) : core;
    }

    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);
    public static bool operator <(SemVersion? a, SemVersion? b) => a is null ? b is not null : a.CompareTo(b) < 0;
    public static bool operator >(SemVersion? a, SemVersion? b) => a is not null && a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion? a, SemVersion? b) => !(a > b);
    public static bool operator >=(SemVersion? a, SemVersion? b) => !(a < b);
}

/// <summary>The running build: version + optional commit (SPEC 20.2).</summary>
/// <param name="Version">The product version.</param>
/// <param name="Commit">Lower-case hex, 7 to 40 chars, or null.</param>
public sealed record AppVersionInfo(SemVersion Version, string? Commit)
{
    private static readonly Regex CommitGrammar = new(
        "^[0-9a-fA-F]{7,40}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static AppVersionInfo Unknown { get; } = new(SemVersion.Zero, null);

    /// <summary>First 7 chars of Commit, or null.</summary>
    public string? ShortCommit => Commit?[..Math.Min(7, Commit.Length)];

    /// <summary>"0.0.6+ce8976..." gives (0.0.6, "ce8976..."). Never throws.</summary>
    public static AppVersionInfo Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return Unknown;
        }
        var text = informationalVersion.Trim();
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var left = plus < 0 ? text : text[..plus];
        var right = plus < 0 ? null : text[(plus + 1)..];
        var version = SemVersion.TryParse(left, out var v) ? v : SemVersion.Zero;
        string? commit = null;
        if (right is not null)
        {
            try
            {
                if (CommitGrammar.IsMatch(right))
                {
                    commit = right.ToLowerInvariant();
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return new AppVersionInfo(version, commit);
    }

    /// <summary>AssemblyInformationalVersionAttribute, else AssemblyName.Version as major.minor.build. Never throws.</summary>
    public static AppVersionInfo FromAssembly(Assembly? assembly)
    {
        if (assembly is null)
        {
            return Unknown;
        }
        try
        {
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                return Parse(info);
            }
            var ver = assembly.GetName().Version;
            if (ver is not null)
            {
                return Parse(string.Create(CultureInfo.InvariantCulture,
                    $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}"));
            }
        }
        catch (Exception)
        {
        }
        return Unknown;
    }
}
