namespace ClippysRevenge.Core.Tests;

public class StyleSkillReasonLineTests
{
    [Fact]
    public void A_kept_reason_line_in_a_sent_mail_is_not_sampled()
    {
        var body = DraftHtml.ToPlainText(DraftReasonLine.Html("SAMPLE-NOTE-9 asked") + "<p>Yes, Friday works for me, see you then.</p>");
        Assert.StartsWith("Clippy: ", body);
        var own = StyleCorpus.OwnText(body, null);
        Assert.DoesNotContain("SAMPLE-NOTE-9", own);
        Assert.Contains("Friday works", own);
        var rows = new[] { new SentMail("s1", "<s1>", DateTimeOffset.Parse("2026-11-01T10:00:00Z"), "Lunch", "ann@x.org", null, body) };
        Assert.Equal(1, StyleCorpus.CountEligible(rows));
        // Only the line: nothing of the user's own words is left.
        var onlyLine = new[] { new SentMail("s2", "<s2>", DateTimeOffset.Parse("2026-11-01T10:00:00Z"), "Lunch", "ann@x.org", null,
            "Clippy: a note that is quite a bit longer than twenty characters") };
        Assert.Equal(0, StyleCorpus.CountEligible(onlyLine));
    }

    [Fact]
    public void Text_without_a_line_is_unchanged() =>
        Assert.Equal(StyleCorpus.OwnText("Hello there\n\nOn Mon, Bob wrote:\n> x", null), MailText.OwnText("Hello there\n\nOn Mon, Bob wrote:\n> x", null));
}