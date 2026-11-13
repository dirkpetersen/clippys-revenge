using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

public static partial class HtmlBody
{
    [GeneratedRegex(@"<body(?=[\s/>])(?:[^>""']|""[^""]*""|'[^']*')*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodyTag();

    /// Plain text -> escaped HTML paragraphs. Throws ArgumentNullException if text is null.
    public static string FromPlainText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        var open = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (open) { sb.Append("</p>"); open = false; }
                continue;
            }
            if (open) sb.Append("<br>"); else { sb.Append("<p>"); open = true; }
            Encode(line, sb);
        }
        if (open) sb.Append("</p>");
        return sb.ToString();
    }

    /// Inserts fragment right after the first opening <body…> tag, or prepends it.
    /// Throws ArgumentNullException if html or fragment is null.
    public static string InsertAtTop(string html, string fragment)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.Length == 0) return html;
        var m = BodyTag().Match(html);
        if (!m.Success) return fragment + html;
        var end = m.Index + m.Length;
        return string.Concat(html.AsSpan(0, end), fragment, html.AsSpan(end));
    }

    private static void Encode(string line, StringBuilder sb)
    {
        foreach (var c in line)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
