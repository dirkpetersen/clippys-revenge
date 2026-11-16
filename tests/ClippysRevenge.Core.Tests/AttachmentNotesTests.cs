using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class AttachmentNotesTests
{
    private static string L(string s) => "<p>" + DraftHtml.HighlightOpen + "Clippy: " + s + DraftHtml.HighlightClose + "</p>";

    private static AttachmentNote N(AttachmentNoteKind k, string name = "Plan.docx", bool shared = false, string? ext = null) =>
        new(k, name, shared, ext);

    private static string Html(params AttachmentNote?[] notes) => AttachmentNotes.Html(notes);

    // 19
    [Theory]
    [InlineData(AttachmentNoteKind.NotSynced, true, null, "Couldn't read the shared file Plan.docx (not synced to this PC).")]
    [InlineData(AttachmentNoteKind.FromOutside, true, null, "Couldn't read the shared file Plan.docx (Clippy only opens shared files from people in your organization).")]
    [InlineData(AttachmentNoteKind.EarlierEmailGone, false, null, "Couldn't read Plan.docx (the earlier email is no longer in your mailbox).")]
    [InlineData(AttachmentNoteKind.PasswordProtected, false, null, "Couldn't read Plan.docx (it's password-protected).")]
    [InlineData(AttachmentNoteKind.TooLarge, false, null, "Couldn't read Plan.docx (larger than 25 MB).")]
    [InlineData(AttachmentNoteKind.NoText, false, null, "Couldn't read Plan.docx (no text in it, maybe a scan).")]
    [InlineData(AttachmentNoteKind.Format, false, "doc", "Couldn't read Plan.docx (Clippy can't read .doc files).")]
    [InlineData(AttachmentNoteKind.Damaged, false, null, "Couldn't read Plan.docx (the file seems damaged).")]
    [InlineData(AttachmentNoteKind.TookTooLong, false, null, "Couldn't read Plan.docx (it took too long).")]
    [InlineData(AttachmentNoteKind.TooMuch, false, null, "Couldn't read Plan.docx (too much attachment text for one draft).")]
    [InlineData(AttachmentNoteKind.Truncated, false, null, "Read only the start of Plan.docx (it's long).")]
    public void Each_kind(AttachmentNoteKind kind, bool shared, string? ext, string text) =>
        Assert.Equal(L(text), Html(N(kind, shared: shared, ext: ext)));

    // 20
    [Fact]
    public void Shared_is_ignored()
    {
        Assert.Equal(Html(N(AttachmentNoteKind.NotSynced, shared: true)), Html(N(AttachmentNoteKind.NotSynced, shared: false)));
        Assert.Equal(L("Couldn't read Plan.docx (the file seems damaged)."), Html(N(AttachmentNoteKind.Damaged, shared: true)));
    }

    // 21
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D0C")]
    [InlineData("a.b")]
    [InlineData("aaaaaaaaaaa")]
    public void Bad_format_extension_gives_no_line(string? ext)
    {
        var n = N(AttachmentNoteKind.Format, ext: ext);
        Assert.Null(AttachmentNoteTexts.Text(n));
        Assert.Equal("", Html(n));
    }

    [Theory]
    [InlineData("xlsb")]
    [InlineData("7z")]
    public void Good_format_extension_gives_a_line(string ext) =>
        Assert.Equal(L($"Couldn't read Plan.docx (Clippy can't read .{ext} files)."),
            Html(N(AttachmentNoteKind.Format, ext: ext)));

    // 22
    [Fact]
    public void Unknown_and_empty()
    {
        var n = N((AttachmentNoteKind)99);
        Assert.Null(AttachmentNoteTexts.Text(n));
        Assert.Equal("", Html(n));
        Assert.Equal("", AttachmentNotes.Html(null));
        Assert.Equal("", AttachmentNotes.Html([]));
        Assert.Equal("", Html((AttachmentNote?)null));
        Assert.Null(AttachmentNoteTexts.Text(null));
    }

    // 23
    [Fact]
    public void Name_is_escaped()
    {
        var h = Html(N(AttachmentNoteKind.Damaged, "a<b>&\"c.pdf"));
        Assert.Equal(L("Couldn't read a&lt;b&gt;&amp;&quot;c.pdf (the file seems damaged)."), h);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "<p>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "<span"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "</span>"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(h, "</p>"));
    }

    // 24
    [Theory]
    [InlineData("https://evil.example/x.pdf", "[link]")]
    [InlineData("www.evil.example", "[link]")]
    [InlineData("ab‮cd​.pdf", "abcd.pdf")]
    [InlineData("a\r\n\tb.pdf", "a b.pdf")]
    [InlineData("Clippy: x.pdf", "x.pdf")]
    [InlineData("   ", "(no name)")]
    [InlineData(null, "(no name)")]
    [InlineData("​", "(no name)")]
    public void Name_cleaning(string? name, string expected) =>
        Assert.Equal(expected, AttachmentNoteTexts.CleanName(name));

    // 25
    [Fact]
    public void Name_cut()
    {
        Assert.Equal(new string('a', 80) + "…", AttachmentNoteTexts.CleanName(new string('a', 100)));
        Assert.Equal(new string('a', 80), AttachmentNoteTexts.CleanName(new string('a', 80)));
        Assert.Equal(new string('a', 79) + "…", AttachmentNoteTexts.CleanName(new string('a', 79) + "\U0001F600"));
    }

    private static AttachmentNote[] Distinct(int n) =>
        Enumerable.Range(1, n).Select(i => N(AttachmentNoteKind.Damaged, $"f{i}.pdf")).ToArray();

    private static string DamagedLine(int i) => L($"Couldn't read f{i}.pdf (the file seems damaged).");

    // 26
    [Fact]
    public void Limit()
    {
        var seven = AttachmentNotes.Html(Distinct(7));
        Assert.Equal(string.Concat(Enumerable.Range(1, 5).Select(DamagedLine)) + L("2 more files couldn't be read fully."), seven);
        Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(seven, "<p>").Count);
        Assert.Equal(string.Concat(Enumerable.Range(1, 5).Select(DamagedLine)) + L("1 more file couldn't be read fully."),
            AttachmentNotes.Html(Distinct(6)));
        var five = AttachmentNotes.Html(Distinct(5));
        Assert.Equal(string.Concat(Enumerable.Range(1, 5).Select(DamagedLine)), five);
        Assert.DoesNotContain("more file", five);
        Assert.Throws<ArgumentOutOfRangeException>(() => AttachmentNoteTexts.More(0));
        Assert.Equal("3 more files couldn't be read fully.", AttachmentNoteTexts.More(3));
    }

    // 27
    [Fact]
    public void Dedupe()
    {
        var notes = new[]
        {
            N(AttachmentNoteKind.Damaged, "a"), N(AttachmentNoteKind.Damaged, "b"), N(AttachmentNoteKind.Damaged, "c"),
            N(AttachmentNoteKind.Damaged, "b"), N(AttachmentNoteKind.Damaged, "d"), N(AttachmentNoteKind.Damaged, "e"),
        };
        var h = AttachmentNotes.Html(notes);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(h, "<p>").Count);
        Assert.DoesNotContain("more file", h);
        Assert.True(h.IndexOf("(the file seems damaged)", StringComparison.Ordinal) > 0);
        Assert.True(h.IndexOf("Couldn't read b ", StringComparison.Ordinal) < h.IndexOf("Couldn't read c ", StringComparison.Ordinal));

        var both = Html(N(AttachmentNoteKind.Damaged, "z"), N(AttachmentNoteKind.TooLarge, "z"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(both, "<p>").Count);
    }

    // 28
    [Fact]
    public void Notes_without_a_line_do_not_count()
    {
        var notes = Distinct(5).Concat([
            N(AttachmentNoteKind.Format, ext: "D0C"), N(AttachmentNoteKind.Format, ext: null), N(AttachmentNoteKind.Format, ext: "a.b"),
        ]).ToArray();
        var h = AttachmentNotes.Html(notes);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(h, "<p>").Count);
        Assert.DoesNotContain("more file", h);
    }

    // 29
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StripPlain_drops_every_leading_clippy_line(bool withReason)
    {
        var html = (withReason ? DraftReasonLine.Html("Ann asked") : "") + AttachmentNotes.Html(Distinct(7))
            + "<p>Hi</p><p>Clippy: kept</p>";
        var s = DraftReasonLine.StripPlain(DraftHtml.ToPlainText(html));
        Assert.StartsWith("Hi", s);
        Assert.Contains("Clippy: kept", s);
        Assert.DoesNotContain("Couldn't", s);
        Assert.DoesNotContain("Read only", s);
        Assert.DoesNotContain("more file", s);
        Assert.DoesNotContain("Ann asked", s);
    }

    // 30
    [Fact]
    public void NoteFor_to_Html_end_to_end()
    {
        static PromptAttachment A(string name, AttachmentOrigin o, AttachmentKind k, ExtractStatus st, string text, string? reason, int tier) =>
            new(name, o, k, st, text, reason, tier, null, null, DateTimeOffset.UnixEpoch);
        var items = new[]
        {
            A("a.pdf", AttachmentOrigin.Email, AttachmentKind.File, ExtractStatus.Skipped, "", AttachmentReasons.PasswordProtected, 1),
            A("b.docx", AttachmentOrigin.Thread, AttachmentKind.Link, ExtractStatus.Skipped, "", AttachmentReasons.NotSynced, 2),
            A("c.png", AttachmentOrigin.Email, AttachmentKind.File, ExtractStatus.Skipped, "", AttachmentReasons.Image, 1),
            A("d.pdf", AttachmentOrigin.Thread, AttachmentKind.File, ExtractStatus.Truncated, "x", null, 3),
        };
        var notes = items.Select(DraftAttachments.NoteFor).Where(n => n is not null).ToList();
        Assert.Equal(L("Couldn't read a.pdf (it's password-protected).")
            + L("Couldn't read the shared file b.docx (not synced to this PC)."), AttachmentNotes.Html(notes));
    }

    // 31
    [Fact]
    public void Notes_change_the_fingerprint()
    {
        static DraftFingerprintInput F(string html) =>
            new("S", "to", null, null, html, null, 1, "from", 0, ["a.pdf"]);
        var notes = AttachmentNotes.Html(Distinct(2));
        var with = F("<html><body>" + notes + "<p>Hi</p></body></html>");
        var without = F("<html><body><p>Hi</p></body></html>");
        Assert.NotEqual(DraftFingerprint.Compute(without), DraftFingerprint.Compute(with));
        Assert.Equal(DraftFingerprint.Compute(with), DraftFingerprint.Compute(F("<html><body>" + notes + "<p>Hi</p></body></html>")));
    }

    // 32
    [Theory]
    [InlineData("Ann asked about the bus")]
    [InlineData("  Clippy: Clippy: x  ")]
    [InlineData("see https://x.org/a now")]
    [InlineData("a<b>&\"")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("​  ")]
    public void Reason_line_is_unchanged_by_the_refactor(string? x) =>
        Assert.Equal(DraftReasonLine.Html(x), DraftReasonLine.Wrap(DraftReasonLine.CleanText(x, 160)));
}
