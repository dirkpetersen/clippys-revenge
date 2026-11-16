using System.Text;

namespace ClippysRevenge.Core.Tests;

public class EmlTextTests
{
    private static ExtractedText Run(string eml) =>
        AttachmentText.Extract("m.eml", Encoding.Latin1.GetBytes(eml), CancellationToken.None);

    private static ExtractedText RunBytes(byte[] eml) =>
        AttachmentText.Extract("m.eml", eml, CancellationToken.None);

    private const string Hdr = "Subject: Hello\r\nFrom: A <a@x.org>\r\nDate: Tue, 10 Nov 2026 09:12:00 +0100\r\n";
    private const string HdrLines = "Subject: Hello\nFrom: A <a@x.org>\nDate: Tue, 10 Nov 2026 09:12:00 +0100";

    private static string Multi(string subtype, string boundary, params string[] parts) =>
        Hdr + $"Content-Type: multipart/{subtype}; boundary=\"{boundary}\"\r\n\r\n"
        + string.Concat(parts.Select(p => $"--{boundary}\r\n{p}\r\n")) + $"--{boundary}--\r\n";

    [Fact]
    public void Single_part()
    {
        var r = Run(Hdr + "\r\nLine 1\r\nLine 2");
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Equal(HdrLines + "\n\nLine 1\nLine 2", r.Text);
    }

    [Fact]
    public void Multipart_alternative_prefers_plain()
    {
        var r = Run(Multi("alternative", "b1",
            "Content-Type: text/plain\r\n\r\nPlain body", "Content-Type: text/html\r\n\r\n<p>Html body</p>"));
        Assert.Contains("Plain body", r.Text);
        Assert.DoesNotContain("Html body", r.Text);
    }

    [Fact]
    public void Html_only()
    {
        var r = Run(Multi("alternative", "b1", "Content-Type: text/html\r\n\r\n<p>Only <b>html</b></p>"));
        Assert.Contains("Only html", r.Text);
        Assert.DoesNotContain("<p>", r.Text);
        Assert.DoesNotContain("<b>", r.Text);
    }

    [Fact]
    public void Quoted_printable()
    {
        var r = Run(Hdr + "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n"
            + "Gr=C3=BC=C3=9Fe=\r\n und mehr");
        Assert.EndsWith("Grüße und mehr", r.Text);
    }

    [Fact]
    public void Base64()
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Grüße"));
        var r = Run(Hdr + "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" + b64 + "\r\n");
        Assert.EndsWith("Grüße", r.Text);
    }

    [Fact]
    public void Charset_1252_and_unknown()
    {
        var r = Run(Hdr + "Content-Type: text/plain; charset=windows-1252\r\nContent-Transfer-Encoding: 8bit\r\n\r\nGrü");
        Assert.EndsWith("Grü", r.Text);

        var utf8 = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("Grüße"));
        var r2 = Run(Hdr + "Content-Type: text/plain; charset=x-made-up\r\n\r\n" + utf8);
        Assert.EndsWith("Grüße", r2.Text);
    }

    [Fact]
    public void Nested_depth_3_is_read_and_depth_4_is_not()
    {
        string plain = "Content-Type: text/plain\r\n\r\nDeep";
        string related = $"Content-Type: multipart/related; boundary=\"b3\"\r\n\r\n--b3\r\n{plain}\r\n--b3--";
        string alt = $"Content-Type: multipart/alternative; boundary=\"b2\"\r\n\r\n--b2\r\n{related}\r\n--b2--";
        var r = Run(Multi("mixed", "b1", alt));
        Assert.Contains("Deep", r.Text);

        string inner = $"Content-Type: multipart/mixed; boundary=\"b4\"\r\n\r\n--b4\r\n{plain}\r\n--b4--";
        string related2 = $"Content-Type: multipart/related; boundary=\"b3\"\r\n\r\n--b3\r\n{inner}\r\n--b3--";
        string alt2 = $"Content-Type: multipart/alternative; boundary=\"b2\"\r\n\r\n--b2\r\n{related2}\r\n--b2--";
        var deep = Run(Multi("mixed", "b1", alt2));
        Assert.Equal(HdrLines, deep.Text);
    }

    [Fact]
    public void Attachment_parts_are_not_decoded()
    {
        var r = Run(Multi("mixed", "b1",
            "Content-Type: text/plain\r\nContent-Disposition: attachment; filename=a.txt\r\n\r\nATTACHED",
            "Content-Type: text/plain\r\n\r\nBody"));
        Assert.Contains("Body", r.Text);
        Assert.DoesNotContain("ATTACHED", r.Text);
    }

    [Fact]
    public void Encoded_subject_words()
    {
        var r = Run("Subject: =?utf-8?B?R3LDvMOfZQ==?= =?utf-8?Q?_und_mehr?=\r\n\r\nx");
        Assert.StartsWith("Subject: Grüße und mehr", r.Text);
        Assert.Equal("=?bad?B?!!!?=", EmlText.DecodeWords("=?bad?B?!!!?="));
    }

    [Fact]
    public void Bad_base64_gives_header_only()
    {
        var r = Run(Hdr + "Content-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n\r\n!!!not base64!!!\r\n");
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Equal(HdrLines, r.Text);
    }

    [Fact]
    public void No_headers_is_damaged()
    {
        var r = RunBytes(Encoding.ASCII.GetBytes("just text, no header"));
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal("the file seems damaged", r.Reason);
    }

    [Fact]
    public void Folded_header()
    {
        var r = Run("Subject: a\r\n b\r\n\r\nx");
        Assert.StartsWith("Subject: a b", r.Text);
    }
}
