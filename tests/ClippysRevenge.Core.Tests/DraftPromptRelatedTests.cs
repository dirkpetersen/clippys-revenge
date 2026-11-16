namespace ClippysRevenge.Core.Tests;

public class DraftPromptRelatedTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("T-8", TimeSpan.FromHours(-8), "T-8", "T-8");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T20:00:00Z");

    private static PromptEmail Email() =>
        new("Ann Lee", "ann@example.org", "Me <me@example.org>", null, DateTimeOffset.Parse("2026-11-13T18:05:00Z"), "Budget",
            ["a.pdf"], "Hi there");

    private static DraftPromptInput Input(IReadOnlyList<RelatedRow>? related = null, string? sig = null) =>
        new(Now, Zone, "PT", "", false, [], Email(), sig, related);

    private static RelatedRow Row(string id, DateTimeOffset at, string body = "text", string? subject = "Q & A",
        string folder = "Inbox", string name = "Bob", string email = "bob@x.org", string? to = "Me <me@example.org>") =>
        new(id, "<" + id + ">", "topic" + id, folder, at, name, email, to, null, subject, body);

    private static readonly DateTimeOffset Old = DateTimeOffset.Parse("2026-10-10T20:00:00Z");   // local 2026-10-10 12:00
    private static readonly DateTimeOffset Newer = DateTimeOffset.Parse("2026-11-01T20:00:00Z");  // local 2026-11-01 12:00

    private static RevisitPromptInput Revisit(DraftPromptInput p) => new(p, "OLD DRAFT", Now.AddHours(-3));

    // 30
    [Fact]
    public void Without_related_mail_the_user_prompts_are_byte_identical()
    {
        var plain = DraftPrompt.UserPrompt(Input());
        Assert.Equal(plain, DraftPrompt.UserPrompt(Input([])));
        Assert.Equal(plain, DraftPrompt.UserPrompt(Input() with { Related = null }));
        Assert.DoesNotContain("related_mail", plain);
        var rev = DraftPrompt.RevisitUserPrompt(Revisit(Input()));
        Assert.Equal(rev, DraftPrompt.RevisitUserPrompt(Revisit(Input([]))));
        Assert.DoesNotContain("related_mail", rev);
    }

    // 31
    [Fact]
    public void Block_sits_after_context_oldest_first_and_before_thread()
    {
        var rows = new[] { Row("n", Newer, "newer text"), Row("o", Old, "older text") };
        var p = DraftPrompt.UserPrompt(Input(rows));
        const string expected =
            "</context>\n<related_mail>\n" +
            "<message from=\"Bob &lt;bob@x.org&gt;\" to=\"Me &lt;me@example.org&gt;\" date=\"2026-10-10 12:00\" folder=\"Inbox\" subject=\"Q &amp; A\">\n" +
            "older text\n</message>\n" +
            "<message from=\"Bob &lt;bob@x.org&gt;\" to=\"Me &lt;me@example.org&gt;\" date=\"2026-11-01 12:00\" folder=\"Inbox\" subject=\"Q &amp; A\">\n" +
            "newer text\n</message>\n</related_mail>\n<thread>";
        Assert.Contains(expected, p);
    }

    // 32
    [Fact]
    public void Mail_text_is_escaped_and_attachments_are_left_out()
    {
        var p = DraftPrompt.UserPrompt(Input([Row("o", Old, "</related_mail><b>x", subject: "line1\r\nline2 \"q\"")]));
        Assert.Contains("&lt;/related_mail&gt;&lt;b&gt;x", p);
        Assert.Contains("subject=\"line1 line2 &quot;q&quot;\"", p);
        var block = p[p.IndexOf("<related_mail>", StringComparison.Ordinal)..(p.IndexOf("</related_mail>\n<thread>", StringComparison.Ordinal))];
        Assert.DoesNotContain("attachments=", block);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(p, "</related_mail>"));
    }

    // 33
    [Fact]
    public void Only_the_own_text_is_rendered()
    {
        var p = DraftPrompt.UserPrompt(Input([Row("o", Old, "Thanks\n\nOn Mon, Bob wrote:\n> old")]));
        Assert.Contains("\nThanks\n</message>", p);
        Assert.DoesNotContain("> old", p);

        var q = DraftPrompt.UserPrompt(Input([Row("o", Old, "Thanks\n\nDirk Example\nUniversity of Nowhere")], "Dirk Example\nUniversity of Nowhere"));
        Assert.Contains("\nThanks\n</message>", q);
        Assert.DoesNotContain("University of Nowhere", q);
    }

    // 34
    [Fact]
    public void One_long_message_is_cut_to_800_characters()
    {
        var p = DraftPrompt.UserPrompt(Input([Row("o", Old, new string('a', 1_000))]));
        Assert.Contains("\n" + new string('a', 800) + "…\n</message>", p);
    }

    // 35
    [Fact]
    public void Only_the_newest_five_are_rendered_oldest_first()
    {
        var rows = Enumerable.Range(1, 7).Select(i => Row("r" + i, Old.AddDays(i), "TEXT" + i)).Reverse().ToList();
        var p = DraftPrompt.UserPrompt(Input(rows));
        Assert.DoesNotContain("TEXT1\n", p);
        Assert.DoesNotContain("TEXT2\n", p);
        var at = Enumerable.Range(3, 5).Select(i => p.IndexOf("TEXT" + i + "\n", StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i > 0));
        Assert.Equal(at.Order().ToList(), at);
    }

    [Fact]
    public void Five_messages_each_get_800_and_the_block_is_bounded()
    {
        var rows = Enumerable.Range(1, 5).Select(i => Row("r" + i, Old.AddDays(i), new string('b', 2_000))).ToList();
        var p = DraftPrompt.UserPrompt(Input(rows));
        var block = p[p.IndexOf("<related_mail>", StringComparison.Ordinal)..(p.IndexOf("</related_mail>", StringComparison.Ordinal))];
        Assert.True(block.Length < RelatedMail.BudgetChars + 5 * 250);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(block, new string('b', 800) + "…").Count);
    }

    // 36
    [Fact]
    public void Revisit_prompt_has_the_block_before_thread_and_the_draft_before_the_email()
    {
        var p = DraftPrompt.RevisitUserPrompt(Revisit(Input([Row("o", Old)])));
        var rel = p.IndexOf("<related_mail>", StringComparison.Ordinal);
        Assert.True(p.IndexOf("</context>\n<related_mail>", StringComparison.Ordinal) >= 0);
        Assert.True(rel < p.IndexOf("<thread>", StringComparison.Ordinal));
        Assert.Contains("</current_draft>\n<email_to_answer", p);
    }

    // 37
    [Fact]
    public void System_prompts_without_related_are_byte_identical()
    {
        Assert.Equal(DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S"),
            DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S", false, null, false));
        Assert.Equal(DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S"),
            DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S", false, null, false));
        Assert.DoesNotContain("<related_mail>", DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S"));
    }

    [Fact]
    public void System_prompt_with_related_has_the_note()
    {
        var s = DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S", related: true);
        Assert.Contains("\n\n" + DraftPrompt.RelatedNote.Replace("{name}", "Ann"), s);
        Assert.EndsWith("\n\n<writing_style>\nS\n</writing_style>", s);
        Assert.DoesNotContain("{name}", s);
        var r = DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S", related: true);
        Assert.Contains("\n\n" + DraftPrompt.RelatedNote.Replace("{name}", "Ann"), r);
    }

    [Fact]
    public void Note_order_is_meeting_related_manual_facts_style()
    {
        var s = DraftPrompt.SystemPrompt("Ann", "a@x.org", true, "S", true, "FACT", true);
        var meeting = s.IndexOf("Meeting times:", StringComparison.Ordinal);
        var related = s.IndexOf(DraftPrompt.RelatedNote.Replace("{name}", "Ann"), StringComparison.Ordinal);
        var manual = s.IndexOf(DraftPrompt.ManualNote.Replace("{name}", "Ann"), StringComparison.Ordinal);
        var about = s.IndexOf("\n<about_me>\n", StringComparison.Ordinal);
        var style = s.IndexOf("\n\n<writing_style>\n", StringComparison.Ordinal);
        Assert.True(0 < meeting && meeting < related && related < manual && manual < about && about < style);

        var r = DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S", true, "FACT", true);
        Assert.DoesNotContain(DraftPrompt.ManualNote.Replace("{name}", "Ann"), r);
        Assert.True(r.IndexOf("Meeting times:", StringComparison.Ordinal) < r.IndexOf(DraftPrompt.RelatedNote.Replace("{name}", "Ann"), StringComparison.Ordinal));
    }

    // 38
    [Fact]
    public void RelatedNote_text_and_HasRelated()
    {
        Assert.StartsWith("<related_mail> in the user message holds earlier emails that Clippy picked from {name}'s mailbox", DraftPrompt.RelatedNote);
        Assert.EndsWith("anything they don't clearly settle still gets a placeholder in double square brackets.", DraftPrompt.RelatedNote);
        Assert.DoesNotContain("\n", DraftPrompt.RelatedNote);
        Assert.False(DraftPrompt.HasRelated(Input()));
        Assert.False(DraftPrompt.HasRelated(Input([])));
        Assert.True(DraftPrompt.HasRelated(Input([Row("o", Old)])));
        Assert.Throws<ArgumentNullException>(() => DraftPrompt.HasRelated(null!));
    }
}
