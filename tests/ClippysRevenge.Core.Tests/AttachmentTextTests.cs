using System.Text;

namespace ClippysRevenge.Core.Tests;

public class AttachmentTextTests
{
    private static ExtractedText Ex(string name, byte[] bytes) =>
        AttachmentText.Extract(name, bytes, CancellationToken.None);

    private static void AssertSkipped(ExtractedText r, string reason)
    {
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal(reason, r.Reason);
        Assert.Equal("", r.Text);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void FormatOf_table()
    {
        Assert.Equal(AttachmentFormat.Pdf, AttachmentText.FormatOf("a.PDF"));
        Assert.Equal(AttachmentFormat.Word, AttachmentText.FormatOf("b.docm"));
        Assert.Equal(AttachmentFormat.Excel, AttachmentText.FormatOf("c.xltx"));
        Assert.Equal(AttachmentFormat.PowerPoint, AttachmentText.FormatOf("d.ppsx"));
        Assert.Equal(AttachmentFormat.Text, AttachmentText.FormatOf("e.markdown"));
        Assert.Equal(AttachmentFormat.Html, AttachmentText.FormatOf("f.htm"));
        Assert.Equal(AttachmentFormat.Eml, AttachmentText.FormatOf("g.eml"));
        Assert.Equal(AttachmentFormat.Image, AttachmentText.FormatOf("h.JPG"));
        Assert.Equal(AttachmentFormat.UnsupportedNoted, AttachmentText.FormatOf("i.xlsb"));
        Assert.Equal(AttachmentFormat.Unsupported, AttachmentText.FormatOf("j.exe"));
        Assert.Equal(AttachmentFormat.Unsupported, AttachmentText.FormatOf("noext"));
        Assert.Equal(AttachmentFormat.Unsupported, AttachmentText.FormatOf(null));
    }

    [Fact]
    public void Extension_rules()
    {
        Assert.Equal("docx", AttachmentText.Extension("  Report.Final.DOCX "));
        Assert.Equal("gz", AttachmentText.Extension("dir/x.tar.gz"));
        Assert.Equal("", AttachmentText.Extension("a.b\\c"));
        Assert.Equal("", AttachmentText.Extension(null));
    }

    [Fact]
    public void Image_is_skipped()
    {
        AssertSkipped(Ex("photo.png", [1, 2, 3]), "image, not read");
    }

    [Fact]
    public void Old_format_unknown_and_odd_extensions()
    {
        AssertSkipped(Ex("budget.xls", [1]), "Clippy can't read .xls files");
        AssertSkipped(Ex("setup.exe", [1]), "Clippy can't read .exe files");
        AssertSkipped(Ex("README", [1]), "Clippy can't read this kind of file");
        AssertSkipped(Ex("x." + new string('a', 40), [1]), "Clippy can't read this kind of file");
        AssertSkipped(Ex("x.exe;rm", [1]), "Clippy can't read this kind of file");
    }

    [Fact]
    public void Too_large_and_exactly_max()
    {
        AssertSkipped(Ex("a.txt", Enumerable.Repeat((byte)'a', (int)AttachmentText.MaxFileBytes + 1).ToArray()),
            "larger than 25 MB");
        var r = Ex("a.txt", Enumerable.Repeat((byte)'a', (int)AttachmentText.MaxFileBytes).ToArray());
        Assert.Equal(ExtractStatus.Truncated, r.Status);
    }

    [Fact]
    public void Text_encodings()
    {
        Assert.Equal("Grüße", Ex("a.txt", [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("Grüße")]).Text);
        var utf8 = Ex("a.txt", Encoding.UTF8.GetBytes("Grüße\n"));
        Assert.Equal(ExtractStatus.Read, utf8.Status);
        Assert.Equal("Grüße", utf8.Text);
        Assert.Equal("Grüße", Ex("a.txt", [0x47, 0x72, 0xFC, 0xDF, 0x65]).Text);
        Assert.Equal("Hi", Ex("a.txt", [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes("Hi")]).Text);
    }

    [Fact]
    public void Nul_is_damaged()
    {
        AssertSkipped(Ex("a.txt", [0x41, 0x00, 0x42]), "the file seems damaged");
    }

    [Fact]
    public void One_mib_cut_backs_off_to_a_utf8_boundary()
    {
        var bytes = Enumerable.Repeat((byte)'a', 1_048_575).Concat(new byte[] { 0xC3, 0xA9 }).ToArray();
        string? text = PlainText.Decode(bytes, out bool truncated);
        Assert.True(truncated);
        Assert.NotNull(text);
        Assert.DoesNotContain('Ã', text);
        Assert.All(text, c => Assert.Equal('a', c));
        Assert.Equal(1_048_575, text.Length);
    }

    [Fact]
    public void Html_drops_scripts_and_tags()
    {
        var r = Ex("x.html", Encoding.UTF8.GetBytes("<p>Hello</p><script>alert(1)</script><p>World</p>"));
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Contains("Hello", r.Text);
        Assert.Contains("World", r.Text);
        Assert.DoesNotContain("alert", r.Text);
        Assert.DoesNotContain("<", r.Text);
    }

    [Fact]
    public void Finish_controls_and_blank_lines()
    {
        Assert.Equal("ab\ncd\te", AttachmentText.Finish("a\u0001b\r\nc\u001bd\te").Text);
        Assert.Equal("a\n\n\nb", AttachmentText.Finish("a\n\n\n\n\nb\n \n\t\n").Text);
        Assert.Equal("\tx", AttachmentText.Finish("\n\n\tx\n").Text);
    }

    [Fact]
    public void Finish_cut_and_no_cut()
    {
        var (cut, was) = AttachmentText.Finish(new string('x', 30_000));
        Assert.Equal(20_000, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.True(was);

        var r = Ex("a.txt", Encoding.UTF8.GetBytes(new string('x', 30_000)));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.True(r.Truncated);

        var (same, cutNo) = AttachmentText.Finish(new string('x', 20_000));
        Assert.Equal(new string('x', 20_000), same);
        Assert.False(cutNo);
    }

    [Fact]
    public void Finish_does_not_leave_a_lone_high_surrogate()
    {
        var (text, cut) = AttachmentText.Finish(new string('x', 19_998) + string.Concat(Enumerable.Repeat("\U0001F600", 10)));
        Assert.True(cut);
        Assert.True(text.Length <= 20_000);
        Assert.EndsWith("…", text);
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public void Cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AttachmentText.Extract("a.txt", new byte[] { 65 }, cts.Token));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var rnd = new Random(12345);
        var bytes = new byte[64 * 1024];
        rnd.NextBytes(bytes);
        foreach (string name in new[] { "x.pdf", "x.docx", "x.xlsx", "x.pptx", "x.eml" })
        {
            var r = Ex(name, bytes);
            if (name != "x.eml") AssertSkipped(r, "the file seems damaged");
        }
    }

    [Fact]
    public void Magic_mismatches_are_damaged()
    {
        AssertSkipped(Ex("report.pdf", Encoding.ASCII.GetBytes("hello")), "the file seems damaged");
        AssertSkipped(Ex("a.docx", Encoding.ASCII.GetBytes("%PDF-1.4 hello")), "the file seems damaged");
    }

    [Fact]
    public void Encrypted_office_file_is_password_protected()
    {
        var bytes = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(bytes, 0);
        AssertSkipped(Ex("a.xlsx", bytes), "it's password-protected");
    }

    [Fact]
    public void Empty_bytes()
    {
        var txt = Ex("a.txt", []);
        Assert.Equal(ExtractStatus.Read, txt.Status);
        Assert.Equal("", txt.Text);
        AssertSkipped(Ex("a.pdf", []), "the file seems damaged");
        AssertSkipped(Ex("a.docx", []), "the file seems damaged");
        AssertSkipped(Ex("a.eml", []), "the file seems damaged");
    }

    [Fact]
    public void FromMailItem_renders_headers_and_attachments()
    {
        var r = AttachmentText.FromMailItem("Plan", "Ricky <r@example.org>",
            new DateTimeOffset(2026, 11, 10, 9, 12, 0, TimeSpan.FromHours(1)), "Numbers attached.",
            ["a.pdf", "b.xlsx"]);
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Equal("Subject: Plan\nFrom: Ricky <r@example.org>\nDate: 2026-11-10 09:12\nAttachments: a.pdf, b.xlsx\n\nNumbers attached.", r.Text);
    }

    [Fact]
    public void FromMailItem_nulls_and_long_body()
    {
        var r = AttachmentText.FromMailItem(null, null, null, null, []);
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Equal("", r.Text);

        var big = AttachmentText.FromMailItem("S", null, null, new string('x', 25_000), []);
        Assert.Equal(ExtractStatus.Truncated, big.Status);
        Assert.Equal(20_000, big.Text.Length);
    }

    [Fact]
    public void Reasons_are_pinned()
    {
        Assert.Equal("the file seems damaged", AttachmentReasons.Damaged);
        Assert.Equal("it's password-protected", AttachmentReasons.PasswordProtected);
        Assert.Equal("no text in it (maybe a scan)", AttachmentReasons.NoText);
        Assert.Equal("larger than 25 MB", AttachmentReasons.TooLarge);
        Assert.Equal("image, not read", AttachmentReasons.Image);
        Assert.Equal("Clippy can't read this kind of file", AttachmentReasons.UnknownKind);
        Assert.Equal("not synced to this PC", AttachmentReasons.NotSynced);
        Assert.Equal("from outside your organization", AttachmentReasons.FromOutside);
        Assert.Equal("not a OneDrive or SharePoint link", AttachmentReasons.NotMicrosoftLink);
        Assert.Equal("the earlier email is no longer in your mailbox", AttachmentReasons.EarlierEmailGone);
        Assert.Equal("not on the email any more", AttachmentReasons.NotOnEmail);
        Assert.Equal("too many files", AttachmentReasons.TooManyFiles);
        Assert.Equal("too much attachment text for one draft", AttachmentReasons.TooMuchText);
        Assert.Equal("ran out of time for attachments", AttachmentReasons.OutOfTime);
        Assert.Equal("it took too long", AttachmentReasons.TookTooLong);
        Assert.Equal("Outlook isn't running", AttachmentReasons.OutlookNotRunning);
        Assert.Equal("Clippy can't read .rtf files", AttachmentReasons.CantRead("rtf"));
    }

    [Fact]
    public void TextSink_caps()
    {
        var sink = new TextSink(5);
        sink.Append("abc");
        Assert.False(sink.Full);
        sink.Append("defgh");
        Assert.True(sink.Full);
        sink.Append('z');
        Assert.Equal("abcde", sink.ToString());
    }
}
