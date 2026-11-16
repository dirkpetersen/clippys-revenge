using System.Text.Json;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class AttachmentLinkHelpersTests
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("T-8", TimeSpan.FromHours(-8), "T-8", "T-8");

    // ---- LinkPaths ----

    [Fact]
    public void Default_BuildsLinksFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "x");
        Assert.Equal(Path.Combine(root, "ClippysRevenge", "links"), LinkPaths.Default(root).Folder);
        Assert.Throws<ArgumentException>(() => LinkPaths.Default(""));
        Assert.Throws<ArgumentException>(() => LinkPaths.Default("rel"));
    }

    [Fact]
    public void NewFileName_AndIsTempFileName()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var n = LinkPaths.NewFileName();
            Assert.Matches(@"^[0-9a-f]{32}\.bin\z", n);
            Assert.True(seen.Add(n));
            Assert.True(LinkPaths.IsTempFileName(n));
        }
        Assert.False(LinkPaths.IsTempFileName(new string('A', 32) + ".bin"));
        Assert.False(LinkPaths.IsTempFileName(new string('a', 31) + ".bin"));
        Assert.False(LinkPaths.IsTempFileName(new string('a', 32) + ".BIN"));
        Assert.False(LinkPaths.IsTempFileName("x.bin"));
        Assert.False(LinkPaths.IsTempFileName(null));
    }

    // ---- LinkFileName ----

    [Theory]
    [InlineData("report.pdf", "report.pdf", "report.pdf")]
    [InlineData("it's \"final\".pdf", "it's _final_.pdf", "it_s _final_.pdf")]
    [InlineData("-rf.pdf", "-rf.pdf", "_rf.pdf")]
    [InlineData("--x", "--x", "_-x")]
    [InlineData(".bashrc", ".bashrc", "_bashrc")]
    [InlineData("...pdf", "...pdf", "_..pdf")]
    [InlineData("..", "attachment", "attachment")]
    [InlineData(".", "attachment", "attachment")]
    [InlineData("CON.txt", "_CON.txt", "_CON.txt")]
    [InlineData("Bericht für Q3.pdf", "Bericht für Q3.pdf", "Bericht für Q3.pdf")]
    [InlineData("a/b\\c.pdf", "a_b_c.pdf", "a_b_c.pdf")]
    [InlineData("a\nb.pdf", "a_b.pdf", "a_b.pdf")]
    [InlineData(null, "attachment", "attachment")]
    [InlineData("", "attachment", "attachment")]
    [InlineData("   ", "attachment", "attachment")]
    [InlineData("$(rm -rf ~).pdf", "$(rm -rf ~).pdf", "$(rm -rf ~).pdf")]
    public void SafeAndForShell(string? input, string safe, string shell)
    {
        Assert.Equal(safe, LinkFileName.Safe(input));
        Assert.Equal(shell, LinkFileName.ForShell(input));
    }

    // ---- FileDownloadHeaders ----

    private static string Disposition(string? name) =>
        FileDownloadHeaders.Build(name, 1).Single(h => h.Key == "Content-Disposition").Value;

    [Fact]
    public void Build_ExactHeadersInOrder()
    {
        var h = FileDownloadHeaders.Build("report.pdf", 1234);
        Assert.Equal(
        [
            new KeyValuePair<string, string>("Content-Type", "application/octet-stream"),
            new("Content-Disposition", "attachment; filename=\"report.pdf\"; filename*=UTF-8''report.pdf"),
            new("Content-Length", "1234"),
            new("X-Content-Type-Options", "nosniff"),
            new("Cache-Control", "no-store"),
        ], h);
    }

    [Fact]
    public void Build_EncodesNonAsciiAndQuotes()
    {
        Assert.Equal("attachment; filename=\"Bericht f_r Q3 _final_.pdf\"; filename*=UTF-8''Bericht%20f%C3%BCr%20Q3%20_final_.pdf",
            Disposition("Bericht für Q3 \"final\".pdf"));
        Assert.Equal("attachment; filename=\"_ clip.txt\"; filename*=UTF-8''%F0%9F%93%8E%20clip.txt", Disposition("📎 clip.txt"));
        Assert.Equal("attachment; filename=\"a;b,c'd~e.txt\"; filename*=UTF-8''a%3Bb%2Cc%27d~e.txt", Disposition("a;b,c'd~e.txt"));
    }

    [Fact]
    public void Build_NullNameAndNegativeLength()
    {
        var h = FileDownloadHeaders.Build(null, 0);
        Assert.Contains("filename=\"attachment\"", h[1].Value);
        Assert.Equal("0", h[2].Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => FileDownloadHeaders.Build("x", -1));
    }

    [Fact]
    public void AsciiNameAndPercentEncode_EdgeCases()
    {
        Assert.Equal("abc", FileDownloadHeaders.AsciiName("a\"b\\c"));
        Assert.Equal("_x", FileDownloadHeaders.AsciiName("\uD800x"));
        Assert.Equal("attachment", FileDownloadHeaders.AsciiName("\"\\"));
        Assert.Equal("%EF%BF%BD", FileDownloadHeaders.PercentEncode("\uD800"));
    }

    // ---- LinkTexts / AttachmentLinkResult ----

    [Fact]
    public void Url_Validates()
    {
        var tok = AttachmentLinks.NewToken();
        Assert.Equal("http://127.0.0.1:8765/files/" + tok, LinkTexts.Url(8765, tok));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkTexts.Url(0, tok));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkTexts.Url(65536, tok));
        Assert.Throws<ArgumentException>(() => LinkTexts.Url(8765, "bad"));
    }

    [Fact]
    public void Curl_QuotesAndValidates()
    {
        Assert.Equal("curl -fsS -H \"Authorization: Bearer $CLIPPY_TOKEN\" -o 'report.pdf' 'http://x/y'", LinkTexts.Curl("report.pdf", "http://x/y"));
        Assert.Throws<ArgumentException>(() => LinkTexts.Curl("it's.pdf", "http://x/y"));
        Assert.Throws<ArgumentException>(() => LinkTexts.Curl("", "http://x/y"));
        Assert.Throws<ArgumentException>(() => LinkTexts.Curl("a.pdf", "http://x/'y"));
        Assert.Throws<ArgumentException>(() => LinkTexts.Curl("a\n.pdf", "http://x/y"));
    }

    [Fact]
    public void PinnedTexts()
    {
        Assert.Equal("One download only. If curl answers 401, CLIPPY_TOKEN isn't set in this shell: ask the user to copy the auth token from the Clippy tray menu and add export CLIPPY_TOKEN=<token> to ~/.bashrc.", LinkTexts.Note);
        Assert.Equal("Clippy can't use its private download folder. Use get_attachment instead.", LinkTexts.FolderUnusable);
        Assert.Equal("Too many open download links (10). Download them or wait until they expire (10 minutes), then try again.", LinkTexts.TooManyOpen);
        Assert.Equal("The open download links already hold 200 MB. Download them or wait until they expire, then try again.", LinkTexts.TooManyBytes);
        Assert.Equal("Too many download links in the last 10 minutes (20). Try again in 8 min.", LinkTexts.RateLimited(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkTexts.RateLimited(0));
    }

    [Fact]
    public void FormatExpires_UsesZone()
    {
        var at = ManualTimeProvider.T0 + TimeSpan.FromMinutes(10);
        Assert.Equal("2026-06-15T04:10:00-08:00", LinkTexts.FormatExpires(at, Utc8));
        Assert.Equal("2026-06-15T12:10:00+00:00", LinkTexts.FormatExpires(at, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Result_Create_BuildsAllFields()
    {
        var tok = AttachmentLinks.NewToken();
        var link = new AttachmentLink(tok, "C:\\x\\" + LinkPaths.NewFileName(), "it's.pdf", 3,
            ManualTimeProvider.T0, ManualTimeProvider.T0 + TimeSpan.FromMinutes(10));
        var r = AttachmentLinkResult.Create(link, 8765, Utc8);
        var url = "http://127.0.0.1:8765/files/" + tok;
        Assert.Equal(url, r.Url);
        Assert.Equal("it's.pdf", r.FileName);
        Assert.Equal(3, r.Size);
        Assert.Equal("2026-06-15T04:10:00-08:00", r.ExpiresAt);
        Assert.Equal("curl -fsS -H \"Authorization: Bearer $CLIPPY_TOKEN\" -o 'it_s.pdf' '" + url + "'", r.Curl);
        Assert.Equal(LinkTexts.Note, r.Note);
        Assert.Contains("$CLIPPY_TOKEN", r.Curl);

        var names = JsonDocument.Parse(JsonSerializer.Serialize(r)).RootElement.EnumerateObject().Select(p => p.Name);
        Assert.Equal(["url", "filename", "size", "expires_at", "curl", "note"], names);
        Assert.Throws<ArgumentNullException>(() => AttachmentLinkResult.Create(null!, 8765, Utc8));
        Assert.Throws<ArgumentNullException>(() => AttachmentLinkResult.Create(link, 8765, null!));
    }

    // ---- WSL token line, tool description (M17.3) ----

    [Fact]
    public void WslTokenLine_BuildsExportLine()
    {
        Assert.Equal("export CLIPPY_TOKEN=0123abcdef", LinkTexts.WslTokenLine("0123abcdef"));
        var token = AuthToken.Generate();
        var line = LinkTexts.WslTokenLine(token);
        Assert.StartsWith("export CLIPPY_TOKEN=", line);
        Assert.EndsWith(token, line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a$b")]
    [InlineData("a;b")]
    [InlineData("a\nb")]
    [InlineData("\u00e9")]
    public void WslTokenLine_RejectsUnsafeTokens(string? token)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => LinkTexts.WslTokenLine(token!));
        if (!string.IsNullOrEmpty(token)) Assert.DoesNotContain(token, ex.Message);
    }

    [Fact]
    public void MaskedWslTokenLine_IsPinned() =>
        Assert.Equal("export CLIPPY_TOKEN=\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022", LinkTexts.MaskedWslTokenLine);

    [Fact]
    public void ToolDescription_IsPinned()
    {
        Assert.Equal("Makes a one-time download link for one attachment of an email, for use from WSL or another shell. Use the id from search_emails; pass filename or position when the email has more than one attachment. Run the returned curl command in the shell: it saves the file in the current folder. The link works once and expires in 10 minutes. The file content is not returned to you. The shell needs CLIPPY_TOKEN set (see note in the result). To save the file on the Windows side instead, use get_attachment.",
            LinkTexts.ToolDescription);
        foreach (var part in new[] { "10 minutes", "curl", "CLIPPY_TOKEN", "get_attachment", "once" })
            Assert.Contains(part, LinkTexts.ToolDescription);
    }
}
