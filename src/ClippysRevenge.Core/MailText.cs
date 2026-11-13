using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>Plain-text mail helpers for AI drafting (SPEC 19.6, 19.7). Pure; never logs.</summary>
public static partial class MailText
{
    public const string EmailToken = "<email>";
    public const string LinkToken = "<link>";
    public const string PhoneToken = "<phone>";

    private const int MaxMarkerLength = 400;
    private const int MaxWrappedNextLength = 700;

    // ---------------------------------------------------------------- patterns

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^-{3,}\s*(Original Message|Ursprüngliche Nachricht|Message d['’]origine|Mensaje original|Oorspronkelijk bericht|Messaggio originale|Forwarded message|Weitergeleitete Nachricht|Message transféré|Mensaje reenviado)\s*-{3,}\s*$", Ci, 2000)]
    private static partial Regex OriginalMessageRegex();

    [GeneratedRegex(@"^_{8,}\s*$", Ci, 2000)]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"^On\s.{1,300}?\swrote:\s*$", Ci, 2000)]
    private static partial Regex AttributionEnRegex();

    [GeneratedRegex(@"^Am\s.{1,300}?\sschrieb\b.{0,300}:\s*$", Ci, 2000)]
    private static partial Regex AttributionDeRegex();

    [GeneratedRegex(@"^Le\s.{1,300}?\sa\s+écrit\s*:\s*$", Ci, 2000)]
    private static partial Regex AttributionFrRegex();

    [GeneratedRegex(@"^El\s.{1,300}?\sescribió\s*:\s*$", Ci, 2000)]
    private static partial Regex AttributionEsRegex();

    [GeneratedRegex(@"^Op\s.{1,300}?\sschreef\b.{0,300}:\s*$", Ci, 2000)]
    private static partial Regex AttributionNlRegex();

    [GeneratedRegex(@"^Il\s.{1,300}?\sha\s+scritto\s*:\s*$", Ci, 2000)]
    private static partial Regex AttributionItRegex();

    [GeneratedRegex(@"^\*?(From|Von|De|Van|Da)\*?\s*:\*?(\s|$)", Ci, 2000)]
    private static partial Regex FromLabelRegex();

    [GeneratedRegex(@"^\*?(Sent|Date|Gesendet|Datum|Envoyé|Enviado|Fecha|Verzonden|Inviato|Data)\*?\s*:", Ci, 2000)]
    private static partial Regex DateLabelRegex();

    [GeneratedRegex(@"^\*?(To|Subject|An|Betreff|À|A|Objet|Para|Asunto|Aan|Onderwerp|Oggetto)\*?\s*:", Ci, 2000)]
    private static partial Regex ToSubjectLabelRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_@/])<?(?:(?:https?|ftp)://|www\.)[^\s<>""]+>?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 5000)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}._%+'\-])<?[\p{L}\p{N}._%+'\-]+@[\p{L}\p{N}\-]+(?:\.[\p{L}\p{N}\-]+)+>?", RegexOptions.CultureInvariant, 5000)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.\-+/])(?:\+[ \t]?)?\(?\d(?:[ \t.()\-]{0,2}\d)*(?![\p{L}\p{N}_:])", RegexOptions.CultureInvariant, 5000)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^\(?\d{4}[-./]\d{1,2}[-./]\d{1,2}$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DateYmdRegex();

    [GeneratedRegex(@"^\d{1,2}[-./]\d{1,2}[-./]\d{2,4}$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex DateDmyRegex();

    // ---------------------------------------------------------------- OwnText

    /// <summary>
    /// The sender's own text of a plain-text body: quoted history and signature cut, line endings "\n",
    /// trailing blank lines removed, result trimmed. "" when nothing is left.
    /// </summary>
    /// <param name="signatureText">Plain text of the user's default signature (DraftHtml.ToPlainText of it), or null.</param>
    /// <exception cref="ArgumentNullException">body is null.</exception>
    public static string OwnText(string body, string? signatureText = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        string[] lines = SplitLines(body);
        int count = FirstMarker(lines);
        if (count < 0) count = lines.Length;

        // Signature cut 1: the "-- " delimiter.
        for (int i = 0; i < count; i++)
        {
            if (lines[i] == "-- " || lines[i] == "--")
            {
                count = i;
                break;
            }
        }

        // Signature cut 2: the user's known signature.
        if (!string.IsNullOrWhiteSpace(signatureText) && count > 0)
        {
            var sig = new List<string>(2);
            foreach (string l in SplitLines(signatureText))
            {
                string c = Canonical(l);
                if (c.Length == 0) continue;
                sig.Add(c);
                if (sig.Count == 2) break;
            }

            if (sig.Count > 0)
            {
                var canon = new string[count];
                for (int i = 0; i < count; i++) canon[i] = Canonical(lines[i]);

                string? nextNonBlank = null;
                for (int k = count - 1; k >= 0; k--)
                {
                    if (string.Equals(canon[k], sig[0], StringComparison.OrdinalIgnoreCase)
                        && (sig.Count == 1
                            || (nextNonBlank is not null && string.Equals(nextNonBlank, sig[1], StringComparison.OrdinalIgnoreCase))))
                    {
                        count = k;
                        break;
                    }
                    if (canon[k].Length > 0) nextNonBlank = canon[k];
                }
            }
        }

        while (count > 0 && string.IsNullOrWhiteSpace(lines[count - 1])) count--;
        return string.Join('\n', lines, 0, count).Trim();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Canonical(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool space = false;
        foreach (char c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
            }
            else
            {
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string LeftTrim(string line) => line.TrimStart(' ', '\t', ' ');

    /// <summary>Index of the first line that starts quoted history, or -1.</summary>
    private static int FirstMarker(string[] lines)
    {
        int n = lines.Length;
        var m14 = new bool[n];
        for (int i = 0; i < n; i++) m14[i] = IsMarker14(lines, i);

        // rest[i]: every line after i is blank or '>'-quoted, up to the first M1-M4 line (or the end).
        var rest = new bool[n];
        if (n > 0) rest[n - 1] = true;
        for (int i = n - 2; i >= 0; i--)
        {
            if (m14[i + 1]) rest[i] = true;
            else if (string.IsNullOrWhiteSpace(lines[i + 1]) || LeftTrim(lines[i + 1]).StartsWith('>')) rest[i] = rest[i + 1];
            else rest[i] = false;
        }

        for (int i = 0; i < n; i++)
        {
            if (m14[i]) return i;
            if (rest[i] && LeftTrim(lines[i]).StartsWith('>')) return i;
        }
        return -1;
    }

    private static bool IsMarker14(string[] lines, int i)
    {
        string t = LeftTrim(lines[i]);
        if (t.Length == 0 || t.Length > MaxMarkerLength) return false;
        char c = t[0];
        if (c == '-') return OriginalMessageRegex().IsMatch(t);
        if (c == '_') return UnderscoreRegex().IsMatch(t);
        if (!char.IsLetter(c) && c != '*') return false;

        if (IsAttribution(t, i + 1 < lines.Length ? lines[i + 1] : null)) return true;
        return IsHeaderBlock(lines, i, t);
    }

    private static bool IsAttribution(string t, string? next)
    {
        if (t.Length < 3 || !char.IsWhiteSpace(t[2])) return false;
        Regex? r = char.ToLowerInvariant(t[0]) switch
        {
            'o' when char.ToLowerInvariant(t[1]) == 'n' => AttributionEnRegex(),
            'a' when char.ToLowerInvariant(t[1]) == 'm' => AttributionDeRegex(),
            'l' when char.ToLowerInvariant(t[1]) == 'e' => AttributionFrRegex(),
            'e' when char.ToLowerInvariant(t[1]) == 'l' => AttributionEsRegex(),
            'o' when char.ToLowerInvariant(t[1]) == 'p' => AttributionNlRegex(),
            'i' when char.ToLowerInvariant(t[1]) == 'l' => AttributionItRegex(),
            _ => null,
        };
        if (r is null) return false;
        if (r.IsMatch(t)) return true;
        if (next is null) return false;

        string n = next.Trim();
        if (n.Length == 0 || n.Length > MaxWrappedNextLength) return false;
        return r.IsMatch(t + " " + n);
    }

    private static bool IsHeaderBlock(string[] lines, int i, string t)
    {
        if (!FromLabelRegex().IsMatch(t)) return false;
        bool date = false, toSubject = false;
        int last = Math.Min(lines.Length - 1, i + 4);
        for (int j = i + 1; j <= last; j++)
        {
            string lt = LeftTrim(lines[j]);
            if (lt.Length == 0 || lt.Length > MaxMarkerLength) continue;
            if (!date && DateLabelRegex().IsMatch(lt)) date = true;
            if (!toSubject && ToSubjectLabelRegex().IsMatch(lt)) toSubject = true;
            if (date && toSubject) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- Redact

    /// <summary>Replaces URLs with &lt;link&gt;, email addresses with &lt;email&gt;, phone-like runs with &lt;phone&gt;.</summary>
    /// <exception cref="ArgumentNullException">text is null.</exception>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;
        string s = LinkRegex().Replace(text, ReplaceLink);
        s = EmailRegex().Replace(s, ReplaceEmail);
        return PhoneRegex().Replace(s, ReplacePhone);
    }

    private static string ReplaceLink(Match m)
    {
        string v = m.Value;
        bool lt = v[0] == '<';
        bool gt = v[^1] == '>';
        if (lt && gt) return LinkToken;

        int start = lt ? 1 : 0;
        int end = gt ? v.Length - 1 : v.Length;
        string core = v.Substring(start, end - start);

        var trailing = new StringBuilder();
        while (core.Length > 0)
        {
            char last = core[^1];
            bool strip = last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"';
            if (!strip && last == ')') strip = Count(core, ')') > Count(core, '(');
            if (!strip && last == ']') strip = Count(core, ']') > Count(core, '[');
            if (!strip) break;
            trailing.Insert(0, last);
            core = core[..^1];
        }

        return (lt ? "<" : "") + LinkToken + trailing + (gt ? ">" : "");
    }

    private static int Count(string s, char c)
    {
        int n = 0;
        foreach (char x in s) if (x == c) n++;
        return n;
    }

    private static string ReplaceEmail(Match m)
    {
        string v = m.Value;
        bool lt = v[0] == '<';
        bool gt = v[^1] == '>';
        if (lt && gt) return EmailToken;
        return (lt ? "<" : "") + EmailToken + (gt ? ">" : "");
    }

    private static string ReplacePhone(Match m)
    {
        string v = m.Value;
        int digits = 0;
        foreach (char c in v) if (char.IsAsciiDigit(c)) digits++;
        if (digits < 7) return v;
        if (DateYmdRegex().IsMatch(v) || DateDmyRegex().IsMatch(v)) return v;
        return PhoneToken;
    }

    // ---------------------------------------------------------------- Headers

    /// <summary>
    /// Parses RFC 5322 transport headers: unfolded, names case-insensitive (OrdinalIgnoreCase),
    /// first value wins. Null/empty/garbage gives an empty dictionary. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Headers(string? transportHeaders)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(transportHeaders)) return result;

        var entries = new List<(string Name, StringBuilder Value)>();
        StringBuilder? current = null;
        foreach (string line in SplitLines(transportHeaders))
        {
            if (line.Length == 0) break;

            if (line[0] is ' ' or '\t')
            {
                current?.Append(' ').Append(line.TrimStart(' ', '\t'));
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0 || !ValidName(line, colon))
            {
                current = null;
                continue;
            }

            current = new StringBuilder(line[(colon + 1)..]);
            entries.Add((line[..colon], current));
        }

        foreach (var (name, value) in entries) result.TryAdd(name, value.ToString().Trim());
        return result;
    }

    private static bool ValidName(string line, int length)
    {
        for (int i = 0; i < length; i++)
        {
            char c = line[i];
            if (c < '!' || c > '~' || c == ':') return false;
        }
        return true;
    }
}
