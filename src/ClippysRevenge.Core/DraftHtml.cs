using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>Model HTML to safe draft fragment (SPEC 19.8). Pure; never throws; never logs.</summary>
public static partial class DraftHtml
{
    public const int MaxInputChars = 200_000;       // longer input is cut to this before tokenizing
    public const int MaxDepth = 64;                 // deeper allowed start tags are dropped (text kept)
    public const int MaxPlaceholderChars = 200;     // inner length of [[...]]
    public const string HighlightOpen = "<span style=\"background-color:#ffff00\">";
    public const string HighlightClose = "</span>";

    private const int MaxPlainTextInputChars = 1_048_576;

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "p", "br", "ul", "ol", "li", "b", "strong", "i", "em", "u", "blockquote",
    };

    private static readonly HashSet<string> SanitizeBreakTags = new(StringComparer.Ordinal)
    {
        "div", "h1", "h2", "h3", "h4", "h5", "h6", "table", "tr", "pre", "hr",
        "section", "article", "header", "footer", "address",
    };

    private static readonly HashSet<string> PlainTextSoftBreakTags = new(StringComparer.Ordinal)
    {
        "p", "div", "li", "tr", "table", "ul", "ol", "blockquote",
        "h1", "h2", "h3", "h4", "h5", "h6", "pre", "hr",
    };

    private static readonly HashSet<string> DroppedWithContent = new(StringComparer.Ordinal)
    {
        "script", "style", "head", "title", "iframe", "object", "embed", "svg", "math",
        "template", "noscript", "textarea",
    };

    [GeneratedRegex(@"\[\[([^\]\r\n<]{1,200})\]\]", RegexOptions.CultureInvariant, 5000)]
    private static partial Regex PlaceholderRegex();

    // ---------------------------------------------------------------- Sanitize

    /// <summary>Allow-list sanitizer plus placeholder highlight. "" when there is no visible text. Null gives "".</summary>
    public static string Sanitize(string? modelHtml)
    {
        if (string.IsNullOrEmpty(modelHtml)) return "";
        string s = modelHtml.Length > MaxInputChars ? modelHtml[..MaxInputChars] : modelHtml;

        var sb = new StringBuilder(s.Length + 16);
        var stack = new List<string>();
        bool lastWasText = false, pendingBreak = false, anyVisible = false;
        var tokenizer = new Tokenizer(s);

        while (tokenizer.Next(out Token t))
        {
            switch (t.Kind)
            {
                case TokenKind.Text:
                {
                    string esc = EscapeText(t.Value);
                    if (esc.Length == 0) break;
                    if (HasVisible(esc))
                    {
                        if (pendingBreak && lastWasText) sb.Append("<br>");
                        pendingBreak = false;
                        anyVisible = true;
                    }
                    sb.Append(esc);
                    lastWasText = true;
                    break;
                }
                case TokenKind.Start:
                    if (t.Value == "br")
                    {
                        sb.Append("<br>");
                        lastWasText = false;
                        pendingBreak = false;
                    }
                    else if (Allowed.Contains(t.Value))
                    {
                        if (stack.Count < MaxDepth)
                        {
                            sb.Append('<').Append(t.Value).Append('>');
                            stack.Add(t.Value);
                            lastWasText = false;
                            pendingBreak = false;
                        }
                    }
                    else if (SanitizeBreakTags.Contains(t.Value))
                    {
                        pendingBreak = true;
                    }
                    break;
                case TokenKind.End:
                    if (t.Value == "br")
                    {
                        // stray </br> is dropped
                    }
                    else if (Allowed.Contains(t.Value))
                    {
                        int idx = stack.LastIndexOf(t.Value);
                        if (idx >= 0)
                        {
                            for (int i = stack.Count - 1; i >= idx; i--)
                            {
                                sb.Append("</").Append(stack[i]).Append('>');
                            }
                            stack.RemoveRange(idx, stack.Count - idx);
                            lastWasText = false;
                            pendingBreak = false;
                        }
                    }
                    else if (SanitizeBreakTags.Contains(t.Value))
                    {
                        pendingBreak = true;
                    }
                    break;
            }
        }

        for (int i = stack.Count - 1; i >= 0; i--) sb.Append("</").Append(stack[i]).Append('>');

        if (!anyVisible) return "";

        string result = sb.ToString();
        try
        {
            return PlaceholderRegex().Replace(result, static m => HighlightOpen + m.Value + HighlightClose);
        }
        catch (RegexMatchTimeoutException)
        {
            return result;
        }
    }

    private static bool HasVisible(string escaped)
    {
        foreach (char c in WebUtility.HtmlDecode(escaped))
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '​' or '‌' or '‍' or '⁠' or '﻿') continue;
            return true;
        }
        return false;
    }

    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        var clean = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
            if (c == 0x7F) continue;
            clean.Append(c);
        }
        string s = clean.ToString();

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&':
                    int len = EntityLength(s, i);
                    if (len > 0)
                    {
                        sb.Append(s, i, len);
                        i += len - 1;
                    }
                    else
                    {
                        sb.Append("&amp;");
                    }
                    break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Length of a valid entity starting at the '&amp;' at <paramref name="i"/>, or 0.</summary>
    private static int EntityLength(string s, int i)
    {
        int p = i + 1;
        if (p >= s.Length) return 0;

        if (s[p] == '#')
        {
            p++;
            bool hex = p < s.Length && (s[p] == 'x' || s[p] == 'X');
            if (hex) p++;
            int digitsStart = p;
            long value = 0;
            int maxDigits = hex ? 6 : 7;
            while (p < s.Length && p - digitsStart < maxDigits && (hex ? char.IsAsciiHexDigit(s[p]) : char.IsAsciiDigit(s[p])))
            {
                value = value * (hex ? 16 : 10) + (char.IsAsciiDigit(s[p]) ? s[p] - '0' : char.ToLowerInvariant(s[p]) - 'a' + 10);
                p++;
            }
            if (p == digitsStart || p >= s.Length || s[p] != ';') return 0;
            bool ok = value is 0x09 or 0x0A or 0x0D
                || (value >= 0x20 && value <= 0x10FFFF && !(value >= 0xD800 && value <= 0xDFFF) && !(value >= 0x7F && value <= 0x9F));
            return ok ? p + 1 - i : 0;
        }

        if (!char.IsAsciiLetter(s[p])) return 0;
        int nameStart = p;
        while (p < s.Length && p - nameStart < 32 && char.IsAsciiLetterOrDigit(s[p])) p++;
        int nameLen = p - nameStart;
        if (nameLen < 2 || p >= s.Length || s[p] != ';') return 0;
        string entity = s.Substring(i, p + 1 - i);
        return WebUtility.HtmlDecode(entity) != entity ? entity.Length : 0;
    }

    // ---------------------------------------------------------------- ToPlainText

    /// <summary>
    /// HTML to plain text for signature matching: dropped-with-content elements removed, block tags and
    /// br become line breaks, entities decoded, HTML whitespace collapsed. Null gives "". Never throws.
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string s = html.Length > MaxPlainTextInputChars ? html[..MaxPlainTextInputChars] : html;

        var sb = new StringBuilder(s.Length);
        bool softPending = false;
        var tokenizer = new Tokenizer(s);
        while (tokenizer.Next(out Token t))
        {
            if (t.Kind == TokenKind.Text)
            {
                string text = CollapseText(t.Value);
                if (text.Length == 0) continue;
                if (text == " " && (softPending || sb.Length == 0 || sb[^1] is ' ' or '\n')) continue;

                if (softPending)
                {
                    if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                    softPending = false;
                }
                if (text[0] == ' ' && sb.Length > 0 && sb[^1] == ' ') text = text[1..];
                sb.Append(text);
            }
            else if (t.Value == "br")
            {
                if (t.Kind == TokenKind.Start)
                {
                    sb.Append('\n');
                    softPending = false;
                }
            }
            else if (PlainTextSoftBreakTags.Contains(t.Value))
            {
                softPending = true;
            }
        }

        var lines = sb.ToString().Split('\n');
        var outLines = new List<string>(lines.Length);
        int blanks = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                if (++blanks > 1) continue;
            }
            else
            {
                blanks = 0;
            }
            outLines.Add(line);
        }
        return string.Join('\n', outLines).Trim();
    }

    private static string CollapseText(string raw)
    {
        string decoded = WebUtility.HtmlDecode(raw);
        var sb = new StringBuilder(decoded.Length);
        bool space = false;
        foreach (char ch in decoded)
        {
            char c = ch == ' ' ? ' ' : ch;
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                space = true;
                continue;
            }
            if (c < 0x20) continue;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        if (space) sb.Append(' ');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- tokenizer

    private enum TokenKind { Text, Start, End }

    /// <summary>For Text the raw text; for tags the lower-case name.</summary>
    private readonly record struct Token(TokenKind Kind, string Value, bool SelfClosing);

    private sealed class Tokenizer
    {
        private const int CappedScan = 1024;

        private readonly string _s;
        private readonly int _lastGt;
        private readonly long _failBudget;
        private long _failed;
        private int _cap = int.MaxValue;
        private int _pos;

        public Tokenizer(string s)
        {
            _s = s;
            _lastGt = s.LastIndexOf('>');
            _failBudget = 4L * s.Length + 10_000;
        }

        public bool Next(out Token token)
        {
            while (_pos < _s.Length)
            {
                if (_s[_pos] != '<')
                {
                    int end = _s.IndexOf('<', _pos);
                    if (end < 0) end = _s.Length;
                    token = new Token(TokenKind.Text, _s.Substring(_pos, end - _pos), false);
                    _pos = end;
                    return true;
                }

                switch (TryMarkup(out token))
                {
                    case Markup.Token:
                        return true;
                    case Markup.Skipped:
                        continue;
                    default:
                    {
                        // A '<' that does not start markup is text, together with what follows up to the next '<'.
                        int end = _s.IndexOf('<', _pos + 1);
                        if (end < 0) end = _s.Length;
                        token = new Token(TokenKind.Text, _s.Substring(_pos, end - _pos), false);
                        _pos = end;
                        return true;
                    }
                }
            }
            token = default;
            return false;
        }

        private enum Markup { Token, Skipped, NotMarkup }

        private Markup TryMarkup(out Token token)
        {
            token = default;
            char next = _pos + 1 < _s.Length ? _s[_pos + 1] : '\0';

            if (next == '!')
            {
                if (string.CompareOrdinal(_s, _pos, "<!--", 0, 4) == 0)
                {
                    int close = _s.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
                    _pos = close < 0 ? _s.Length : close + 3;
                }
                else
                {
                    SkipDeclaration();
                }
                return Markup.Skipped;
            }
            if (next == '?')
            {
                SkipDeclaration();
                return Markup.Skipped;
            }
            if (next == '/' && _pos + 2 < _s.Length && char.IsAsciiLetter(_s[_pos + 2]))
            {
                return ParseTag(_pos + 2, isEnd: true, out token);
            }
            if (char.IsAsciiLetter(next))
            {
                return ParseTag(_pos + 1, isEnd: false, out token);
            }
            return Markup.NotMarkup;
        }

        private void SkipDeclaration()
        {
            int gt = _s.IndexOf('>', _pos + 2);
            _pos = gt < 0 ? _s.Length : gt + 1;
        }

        private Markup ParseTag(int nameStart, bool isEnd, out Token token)
        {
            token = default;
            int p = nameStart + 1;
            while (p < _s.Length && (char.IsAsciiLetterOrDigit(_s[p]) || _s[p] is ':' or '_' or '-')) p++;
            string name = _s.Substring(nameStart, p - nameStart).ToLowerInvariant();

            int gt = ScanToGt(p);
            if (gt < 0) return Markup.NotMarkup;

            bool selfClosing = gt - 1 >= p && _s[gt - 1] == '/';
            _pos = gt + 1;

            if (!isEnd && DroppedWithContent.Contains(name))
            {
                SkipRaw(name);
                return Markup.Skipped;
            }

            token = new Token(isEnd ? TokenKind.End : TokenKind.Start, name, selfClosing);
            return Markup.Token;
        }

        /// <summary>Index of the closing '&gt;' (quote-aware), or -1.</summary>
        private int ScanToGt(int from)
        {
            if (from > _lastGt) return -1;
            int limit = _cap == int.MaxValue ? _s.Length : (int)Math.Min(_s.Length, (long)from + _cap);
            char quote = '\0';
            for (int i = from; i < limit; i++)
            {
                char c = _s[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return i;
                }
            }

            _failed += limit - from;
            if (_failed > _failBudget) _cap = CappedScan;
            return -1;
        }

        private void SkipRaw(string name)
        {
            string close = "</" + name;
            int from = _pos;
            while (from < _s.Length)
            {
                int idx = _s.IndexOf(close, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                int after = idx + close.Length;
                if (after < _s.Length && (char.IsWhiteSpace(_s[after]) || _s[after] is '/' or '>'))
                {
                    int gt = _s.IndexOf('>', after);
                    _pos = gt < 0 ? _s.Length : gt + 1;
                    return;
                }
                from = idx + 1;
            }
            _pos = _s.Length;
        }
    }
}
