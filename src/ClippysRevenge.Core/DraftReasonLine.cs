using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>SPEC 25.4: the highlighted "why this draft" line. Pure; never throws; never logs.</summary>
public static partial class DraftReasonLine
{
    /// <summary>Clippy's prefix of every line it puts at the top of a draft (also M19's attachment notes).</summary>
    public const string Prefix = "Clippy: ";

    /// <summary>Cut length of the note text (before escaping; "…" is added after it).</summary>
    public const int MaxChars = 160;

    /// <summary>Only the first this-many characters of the input are looked at.</summary>
    public const int MaxInputChars = 640;

    /// <summary>Replaces every URL in the note.</summary>
    public const string LinkText = "[link]";

    private const string PrefixWord = "Clippy:";

    [GeneratedRegex(@"(?:(?:https?|ftp|file)://|mailto:|www\.)\S*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex UrlRegex();

    /// <summary>"" for null/blank (or nothing left after cleaning), else exactly
    /// "&lt;p&gt;" + DraftHtml.HighlightOpen + Prefix + escaped text + DraftHtml.HighlightClose + "&lt;/p&gt;".</summary>
    public static string Html(string? note) => Wrap(CleanText(note, MaxChars));

    /// <summary>Steps of Html with maxChars instead of MaxChars: input cap, control to space, format characters and lone
    /// surrogates removed, whitespace collapsed and trimmed, URLs to LinkText, leading "Clippy:" removed, cut. Not
    /// escaped. "" when nothing is left, on a regex timeout, or on any exception. Never throws.</summary>
    internal static string CleanText(string? note, int maxChars)
    {
        try
        {
            if (maxChars < 1 || string.IsNullOrEmpty(note)) return "";
            var n = Math.Min(note.Length, MaxInputChars);
            if (n < note.Length && n > 0 && char.IsHighSurrogate(note[n - 1])) n--;

            var sb = new StringBuilder(n);
            for (var i = 0; i < n; i++)
            {
                var c = note[i];
                if (char.IsControl(c)) { sb.Append(' '); continue; }
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < n && char.IsLowSurrogate(note[i + 1]))
                    {
                        sb.Append(c).Append(note[i + 1]);
                        i++;
                    }
                    continue;
                }
                if (char.IsLowSurrogate(c)) continue;
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
                sb.Append(c);
            }

            // Whitespace runs to one space, trimmed.
            var ws = new StringBuilder(sb.Length);
            var space = false;
            foreach (var c in sb.ToString())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && ws.Length > 0) ws.Append(' ');
                space = false;
                ws.Append(c);
            }
            var text = ws.ToString();

            try
            {
                text = UrlRegex().Replace(text, LinkText);
            }
            catch (RegexMatchTimeoutException)
            {
                return "";
            }

            while (text.StartsWith(PrefixWord, StringComparison.OrdinalIgnoreCase))
                text = text[PrefixWord.Length..].Trim();
            if (text.Length == 0) return "";

            return DraftPrompt.Cut(text, maxChars);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>"" for null/empty, else "&lt;p&gt;" + HighlightOpen + Prefix + Escape(text) + HighlightClose + "&lt;/p&gt;".
    /// No cleaning (the caller cleans). Never throws.</summary>
    internal static string Wrap(string? text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return "";
            return "<p>" + DraftHtml.HighlightOpen + Prefix + DraftPrompt.Escape(text) + DraftHtml.HighlightClose + "</p>";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Plain text of a draft without its leading Clippy lines. Null gives "".
    /// Returns the input unchanged (same string) when it has no leading Clippy line.</summary>
    public static string StripPlain(string? plain)
    {
        if (plain is null) return "";
        var lines = plain.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var skipped = false;
        var i = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var t = line.Trim();
            if (t.Length > PrefixWord.Length && t.StartsWith(PrefixWord, StringComparison.Ordinal)
                && char.IsWhiteSpace(t[PrefixWord.Length]))
            {
                skipped = true;
                continue;
            }
            break;
        }
        if (!skipped) return plain;
        return string.Join("\n", lines.Skip(i));
    }
}
