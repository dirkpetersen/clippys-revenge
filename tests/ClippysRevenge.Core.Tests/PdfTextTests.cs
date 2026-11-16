namespace ClippysRevenge.Core.Tests;

public class PdfTextTests
{
    private static ExtractedText Ex(byte[] bytes) => AttachmentText.Extract("a.pdf", bytes, CancellationToken.None);

    [Fact]
    public void Two_pages_in_order()
    {
        var r = Ex(PdfFixtures.Pages("Alpha fact 41", "Beta fact 42"));
        Assert.Equal(ExtractStatus.Read, r.Status);
        int a = r.Text.IndexOf("Alpha fact 41", StringComparison.Ordinal);
        int b = r.Text.IndexOf("Beta fact 42", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a);
        Assert.Contains("\n\n", r.Text[a..b]);
    }

    [Fact]
    public void Page_cap_is_30()
    {
        var r = Ex(PdfFixtures.Pages(Enumerable.Range(1, 31).Select(i => $"Page {i} marker").ToArray()));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.Contains("Page 30 marker", r.Text);
        Assert.DoesNotContain("Page 31 marker", r.Text);
    }

    [Fact]
    public void Exactly_30_pages_is_read()
    {
        var r = Ex(PdfFixtures.Pages(Enumerable.Range(1, 30).Select(i => $"Page {i} marker").ToArray()));
        Assert.Equal(ExtractStatus.Read, r.Status);
    }

    [Fact]
    public void Pages_without_text_are_no_text()
    {
        var r = Ex(PdfFixtures.Blank(2));
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal("no text in it (maybe a scan)", r.Reason);
    }

    [Fact]
    public void Encrypted_pdf_is_password_protected()
    {
        var r = Ex(PdfFixtures.Encrypted());
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal("it's password-protected", r.Reason);
    }

    [Fact]
    public void Magic_may_sit_within_the_first_1024_bytes()
    {
        var pdf = PdfFixtures.Pages("Offset fact");
        var bytes = Enumerable.Repeat((byte)'\n', 100).Concat(pdf).ToArray();
        var r = Ex(bytes);
        Assert.NotEqual("the file seems damaged", r.Reason);
    }

    [Fact]
    public void Garbage_after_the_header_is_damaged()
    {
        var rnd = new Random(7);
        var junk = new byte[1000];
        rnd.NextBytes(junk);
        var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n").Concat(junk).ToArray();
        var r = Ex(bytes);
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal("the file seems damaged", r.Reason);
    }

    [Fact]
    public void Char_cap_truncates()
    {
        var r = Ex(PdfFixtures.Pages(Enumerable.Range(1, 30).Select(_ => new string('x', 1000)).ToArray()));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.True(r.Text.Length <= 20_000);
    }
}
