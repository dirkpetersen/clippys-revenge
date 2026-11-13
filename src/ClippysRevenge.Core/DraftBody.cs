namespace ClippysRevenge.Core;

public static class DraftBody
{
    public static string? SignatureContent(string? signatureHtml)
    {
        if (signatureHtml is null) return null;
        var c = HtmlBody.BodyContent(signatureHtml);
        return string.IsNullOrWhiteSpace(c) ? null : c;
    }

    public static string Fragment(string body, bool html, string? signatureHtml)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = html ? body : HtmlBody.FromPlainText(body);
        var s = SignatureContent(signatureHtml);
        if (s is null) return text;
        return text.Length == 0 ? s : text + "<br>" + s;
    }
}
