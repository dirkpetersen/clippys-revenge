using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>Builds the reply subject. The double colon in "Re:: " is intentional (SPEC §1, §11.6).</summary>
public static partial class ReplySubject
{
    public const string Prefix = "Re:: ";

    [GeneratedRegex(@"^(re|fwd|fw|aw|wg):+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingPrefix();

    /// <summary>Never throws. null/whitespace gives "Re::".</summary>
    public static string Normalize(string? subject)
    {
        var s = (subject ?? "").Trim();
        while (true)
        {
            var m = LeadingPrefix().Match(s);
            if (!m.Success) break;
            s = s[m.Length..].TrimStart();
        }
        var rest = s.Trim();
        return rest.Length == 0 ? "Re::" : Prefix + rest;
    }
}
