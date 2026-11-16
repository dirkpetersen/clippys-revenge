using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>Minimal .eml MIME reader (SPEC 26.4). Attachments and nested messages are never decoded.</summary>
internal static partial class EmlText
{
    private const int MaxDepth = 3;

    static EmlText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private sealed class Found
    {
        public string? Plain;
        public string? Html;
    }

    public static ExtractedText Extract(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string raw = Encoding.Latin1.GetString(bytes.Span);
        SplitHeaderBody(raw, out string headerBlock, out string body);
        IReadOnlyDictionary<string, string> headers = MailText.Headers(headerBlock);
        if (headers.Count == 0) return ExtractedText.Skip(AttachmentReasons.Damaged);

        var found = new Found();
        Walk(headers, body, 0, found, ct);
        string text = found.Plain ?? found.Html ?? "";

        string subject = DecodeWords(HeaderText(headers, "Subject"));
        string from = DecodeWords(HeaderText(headers, "From"));
        string date = HeaderText(headers, "Date");
        return AttachmentText.Build(AttachmentText.RenderMessage(subject, from, date, null, text), false);
    }

    // ------------------------------------------------------------------ structure

    private static void SplitHeaderBody(string s, out string headers, out string body)
    {
        int a = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int b = s.IndexOf("\n\n", StringComparison.Ordinal);
        if (s.StartsWith("\r\n", StringComparison.Ordinal))
        {
            headers = "";
            body = s[2..];
        }
        else if (s.StartsWith('\n'))
        {
            headers = "";
            body = s[1..];
        }
        else if (a >= 0 && (b < 0 || a <= b))
        {
            headers = s[..a];
            body = s[(a + 4)..];
        }
        else if (b >= 0)
        {
            headers = s[..b];
            body = s[(b + 2)..];
        }
        else
        {
            headers = s;
            body = "";
        }
    }

    private static void Walk(IReadOnlyDictionary<string, string> headers, string body, int depth, Found found,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth) return;

        string contentType = headers.TryGetValue("Content-Type", out string? ctRaw) ? ctRaw : "text/plain";
        ParseValue(contentType, out string type, out var ctParams);
        if (type.Length == 0) type = "text/plain";

        if (headers.TryGetValue("Content-Disposition", out string? dispRaw))
        {
            ParseValue(dispRaw, out string disp, out _);
            if (disp == "attachment") return;
        }

        if (type.StartsWith("multipart/", StringComparison.Ordinal))
        {
            if (!ctParams.TryGetValue("boundary", out string? boundary) || boundary.Length == 0) return;
            foreach (string part in SplitParts(body, boundary))
            {
                if (found.Plain is not null) return;
                SplitHeaderBody(part, out string ph, out string pb);
                Walk(MailText.Headers(ph), pb, depth + 1, found, ct);
            }
            return;
        }

        if (type == "message/rfc822") return;

        if (type == "text/plain")
        {
            found.Plain ??= DecodeText(headers, ctParams, body) ?? "";
        }
        else if (type == "text/html")
        {
            found.Html ??= DraftHtml.ToPlainText(DecodeText(headers, ctParams, body) ?? "");
        }
    }

    /// <summary>Parts between the boundary lines; the line break before a boundary belongs to the boundary.</summary>
    private static List<string> SplitParts(string body, string boundary)
    {
        var parts = new List<string>();
        string open = "--" + boundary;
        string close = open + "--";
        int partStart = -1;
        int pos = 0;
        while (pos <= body.Length)
        {
            int nl = body.IndexOf('\n', pos);
            int lineEnd = nl < 0 ? body.Length : nl;
            string line = body[pos..lineEnd].TrimEnd();

            bool isClose = line == close;
            bool isOpen = line == open;
            if (isOpen || isClose)
            {
                if (partStart >= 0)
                {
                    int end = pos;                       // start of the boundary line
                    if (end > partStart && body[end - 1] == '\n') end--;
                    if (end > partStart && body[end - 1] == '\r') end--;
                    parts.Add(body[partStart..Math.Max(partStart, end)]);
                }
                if (isClose) return parts;
                partStart = nl < 0 ? body.Length : nl + 1;
            }

            if (nl < 0) break;
            pos = nl + 1;
        }
        // No closing boundary: a part that is still open counts up to the end.
        if (partStart >= 0 && partStart <= body.Length) parts.Add(body[partStart..]);
        return parts;
    }

    // ------------------------------------------------------------------ values

    private static string HeaderText(IReadOnlyDictionary<string, string> headers, string name)
    {
        if (!headers.TryGetValue(name, out string? v)) return "";
        foreach (char c in v)
        {
            if (c < 0x80) continue;
            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Encoding.Latin1.GetBytes(v));
            }
            catch (Exception)
            {
                return v;
            }
        }
        return v;
    }

    /// <summary>"type/subtype; a=b; c=\"d\"" into lower-case main value and a case-insensitive parameter map.</summary>
    private static void ParseValue(string value, out string main, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var segments = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in value)
        {
            if (c == '"') quoted = !quoted;
            if (c == ';' && !quoted)
            {
                segments.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        segments.Add(sb.ToString());

        main = segments[0].Trim().ToLowerInvariant();
        for (int i = 1; i < segments.Count; i++)
        {
            string seg = segments[i];
            int eq = seg.IndexOf('=');
            if (eq <= 0) continue;
            string name = seg[..eq].Trim();
            string val = seg[(eq + 1)..].Trim();
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') val = val[1..^1];
            if (name.Length > 0) parameters.TryAdd(name, val);
        }
    }

    // ------------------------------------------------------------------ body decoding

    /// <summary>Decoded text of a text part, or null when its transfer encoding is broken.</summary>
    private static string? DecodeText(IReadOnlyDictionary<string, string> headers,
        Dictionary<string, string> ctParams, string body)
    {
        string cte = headers.TryGetValue("Content-Transfer-Encoding", out string? c) ? c.Trim().ToLowerInvariant() : "";
        byte[] data;
        if (cte == "base64")
        {
            var compact = new StringBuilder(body.Length);
            foreach (char ch in body)
                if (!char.IsWhiteSpace(ch)) compact.Append(ch);
            data = new byte[compact.Length * 3 / 4 + 3];
            if (!Convert.TryFromBase64String(compact.ToString(), data, out int written)) return null;
            data = data[..written];
        }
        else if (cte == "quoted-printable")
        {
            data = DecodeQuotedPrintable(body);
        }
        else
        {
            data = Encoding.Latin1.GetBytes(body);
        }

        Encoding enc = Encoding.UTF8;
        if (ctParams.TryGetValue("charset", out string? charset) && charset.Length > 0)
            enc = EncodingOrUtf8(charset);
        return enc.GetString(data);
    }

    private static Encoding EncodingOrUtf8(string charset)
    {
        try
        {
            int star = charset.IndexOf('*');
            return Encoding.GetEncoding(star > 0 ? charset[..star] : charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    private static byte[] DecodeQuotedPrintable(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '=')
            {
                bytes.Add((byte)c);
                continue;
            }
            if (i + 2 < s.Length && Hex(s[i + 1]) >= 0 && Hex(s[i + 2]) >= 0)
            {
                bytes.Add((byte)(Hex(s[i + 1]) * 16 + Hex(s[i + 2])));
                i += 2;
            }
            else if (i + 1 < s.Length && s[i + 1] == '\n')
            {
                i += 1;                                       // soft line break (LF)
            }
            else if (i + 2 < s.Length && s[i + 1] == '\r' && s[i + 2] == '\n')
            {
                i += 2;                                       // soft line break (CRLF)
            }
            else
            {
                bytes.Add((byte)'=');
            }
        }
        return bytes.ToArray();
    }

    // ------------------------------------------------------------------ RFC 2047

    [GeneratedRegex(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=")]
    private static partial Regex EncodedWord();

    /// <summary>RFC 2047 encoded-words (B and Q) in a header value; invalid words stay raw. Never throws.</summary>
    public static string DecodeWords(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("=?", StringComparison.Ordinal)) return value ?? "";
        try
        {
            var sb = new StringBuilder();
            int last = 0;
            bool prevDecoded = false;
            foreach (Match m in EncodedWord().Matches(value))
            {
                string? decoded = DecodeWord(m);
                string gap = value[last..m.Index];
                if (decoded is null)
                {
                    sb.Append(gap).Append(m.Value);
                    prevDecoded = false;
                }
                else
                {
                    if (!(prevDecoded && string.IsNullOrWhiteSpace(gap))) sb.Append(gap);
                    sb.Append(decoded);
                    prevDecoded = true;
                }
                last = m.Index + m.Length;
            }
            sb.Append(value[last..]);
            return sb.ToString();
        }
        catch (Exception)
        {
            return value;
        }
    }

    private static string? DecodeWord(Match m)
    {
        try
        {
            Encoding enc = EncodingOrUtf8(m.Groups[1].Value);
            string payload = m.Groups[3].Value;
            byte[] data;
            if (m.Groups[2].Value is "B" or "b")
            {
                string padded = payload.Length % 4 == 0 ? payload : payload + new string('=', 4 - payload.Length % 4);
                data = new byte[padded.Length * 3 / 4 + 3];
                if (!Convert.TryFromBase64String(padded, data, out int written)) return null;
                data = data[..written];
            }
            else
            {
                var bytes = new List<byte>(payload.Length);
                for (int i = 0; i < payload.Length; i++)
                {
                    char c = payload[i];
                    if (c == '_') bytes.Add((byte)' ');
                    else if (c == '=' && i + 2 < payload.Length && Hex(payload[i + 1]) >= 0 && Hex(payload[i + 2]) >= 0)
                    {
                        bytes.Add((byte)(Hex(payload[i + 1]) * 16 + Hex(payload[i + 2])));
                        i += 2;
                    }
                    else bytes.Add((byte)c);
                }
                data = bytes.ToArray();
            }
            return enc.GetString(data);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
