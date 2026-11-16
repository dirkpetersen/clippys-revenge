namespace ClippysRevenge.Core.Tests;

public class DraftPromptReasonTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("T-8", TimeSpan.FromHours(-8), "T-8", "T-8");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T20:00:00Z");

    private static string Note(string name = "Ann") => DraftPrompt.ReasonNote.Replace("{name}", name);

    private static PromptEmail Email() =>
        new("Ann Lee", "ann@example.org", "Me <me@example.org>", null, Now.AddHours(-2), "Budget", [], "Hi there");

    private static DraftPromptInput Input(IReadOnlyList<ThreadMessage>? thread = null, IReadOnlyList<RelatedRow>? related = null) =>
        new(Now, Zone, "PT", "", false, thread ?? [], Email(), null, related);

    private static ThreadMessage Msg(string folder, string body) =>
        new("t" + folder, folder, Now.AddHours(-5), "Dirk", "dirk@x.org", "ann@example.org", null, "Budget", [], body);

    [Fact]
    public void ReasonNote_text_is_pinned()
    {
        Assert.DoesNotContain('\n', DraftPrompt.ReasonNote);
        Assert.Contains("{name}", DraftPrompt.ReasonNote);
        Assert.DoesNotContain("{smtp}", DraftPrompt.ReasonNote);
        Assert.StartsWith("note_for_user: when you write a reply, also set note_for_user to one short sentence for {name}", DraftPrompt.ReasonNote);
        Assert.EndsWith("When you don't write a reply, leave note_for_user out.", DraftPrompt.ReasonNote);
        Assert.Contains("don't start it with \"Clippy:\"", DraftPrompt.ReasonNote);
    }

    [Fact]
    public void Reason_off_is_byte_identical()
    {
        Assert.Equal(DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S"),
            DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S", false, null, false, false));
        Assert.Equal(DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S"),
            DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S", false, null, false, false));
    }

    [Fact]
    public void Reason_on_adds_the_note_with_the_name_filled_in()
    {
        var p = DraftPrompt.SystemPrompt("Ann", "a@x.org", false, "S", reason: true);
        Assert.Contains("\n\n" + Note(), p);
        Assert.DoesNotContain("{name}", p);
        Assert.Contains("\n\n" + Note(), DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S", reason: true));
    }

    [Fact]
    public void Order_with_everything_on()
    {
        var p = DraftPrompt.SystemPrompt("Ann", "a@x.org", true, "S", true, "F1", true, true);
        var order = new[]
        {
            p.IndexOf("Meeting times:", StringComparison.Ordinal), p.IndexOf(Note(), StringComparison.Ordinal),
            p.IndexOf(DraftPrompt.RelatedNote.Replace("{name}", "Ann"), StringComparison.Ordinal),
            p.IndexOf(DraftPrompt.ManualNote.Replace("{name}", "Ann"), StringComparison.Ordinal),
            p.IndexOf("<about_me>\n", StringComparison.Ordinal), p.IndexOf("\n<writing_style>\n", StringComparison.Ordinal),
        };
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);

        var r = DraftPrompt.RevisitSystemPrompt("Ann", "a@x.org", "S", true, "F1", true, true);
        var ro = new[]
        {
            r.IndexOf("Meeting times:", StringComparison.Ordinal), r.IndexOf(Note(), StringComparison.Ordinal),
            r.IndexOf(DraftPrompt.RelatedNote.Replace("{name}", "Ann"), StringComparison.Ordinal),
            r.IndexOf("<about_me>\n", StringComparison.Ordinal), r.IndexOf("\n<writing_style>\n", StringComparison.Ordinal),
        };
        Assert.All(ro, i => Assert.True(i >= 0));
        Assert.Equal(ro.OrderBy(i => i), ro);
        Assert.DoesNotContain(DraftPrompt.ManualNote.Replace("{name}", "Ann"), r);
    }

    [Fact]
    public void Current_draft_loses_its_clippy_lines()
    {
        var input = new RevisitPromptInput(Input(), "Clippy: OLD-NOTE-77\r\n\r\nHi Ricky,\r\nyes.", Now.AddHours(-3));
        var p = DraftPrompt.RevisitUserPrompt(input);
        Assert.Contains("<current_draft created=\"2026-11-13 09:00\">\nHi Ricky,\nyes.\n</current_draft>", p);
        Assert.DoesNotContain("OLD-NOTE-77", p);
    }

    [Fact]
    public void Current_draft_without_a_line_is_unchanged()
    {
        var input = new RevisitPromptInput(Input(), "Hi Ricky,\r\nyes.", Now.AddHours(-3));
        Assert.Contains("<current_draft created=\"2026-11-13 09:00\">\nHi Ricky,\nyes.\n</current_draft>",
            DraftPrompt.RevisitUserPrompt(input));
    }

    [Fact]
    public void Thread_mail_from_others_keeps_a_clippy_line()
    {
        var p = DraftPrompt.UserPrompt(Input([Msg("Inbox", "Clippy: x\nHello")]));
        Assert.Contains("\nClippy: x\nHello\n", p);
    }

    [Fact]
    public void Own_sent_mail_in_the_thread_loses_a_kept_reason_line()
    {
        var p = DraftPrompt.UserPrompt(Input([Msg("Sent Items", "Clippy: SENT-NOTE-1\n\nSure, Friday works.")]));
        Assert.DoesNotContain("SENT-NOTE-1", p);
        Assert.Contains("\nSure, Friday works.\n", p);
    }

    [Fact]
    public void Own_sent_mail_in_the_related_block_loses_a_kept_reason_line()
    {
        RelatedRow Row(string folder) => new("r" + folder, "<r>", "t", folder, Now.AddDays(-9), "Dirk", "dirk@x.org", "ann@example.org",
            null, "Budget", "Clippy: REL-NOTE-2\n\nThe budget is fine.");
        var sent = DraftPrompt.UserPrompt(Input(related: [Row("Sent Items")]));
        Assert.DoesNotContain("REL-NOTE-2", sent);
        Assert.Contains("\nThe budget is fine.\n", sent);
        Assert.Contains("REL-NOTE-2", DraftPrompt.UserPrompt(Input(related: [Row("Inbox")])));
    }
}