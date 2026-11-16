using System.Text.Json;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerUsageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;
    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    private static ModelResult WithUsage(ModelResult ok, TokenUsage? usage) => ((ModelResult.Ok)ok) with { Usage = usage };

    private static ModelResult Keep() => Ok("""{"action":"keep","confidence":0.9,"reason":"r"}""");

    // 1
    [Fact]
    public async Task Automatic_pass_uses_the_Draft_kind()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));
        await h.Worker.RunPassAsync(Ct);
        Assert.Equal(ModelCallKind.Draft, Assert.Single(h.Model.Requests).Kind);
    }

    // 2
    [Fact]
    public async Task Manual_draft_uses_the_Manual_kind()
    {
        using var h = new DraftHarness();
        h.Ai.Enabled = false;
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1", Folder = "Inbox", Subject = "Budget", SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>", ConversationTopic = "Budget", Body = "BODYTEXT-123", EventTime = Now.AddMinutes(-5),
        });
        h.Model.Enqueue(OkReply(0.9));
        await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(ModelCallKind.Manual, Assert.Single(h.Model.Requests).Kind);
    }

    private static DraftHarness FxRevisit()
    {
        var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Ai.RequireDirectRecipient = false;
        h.Seed("S1", at: Now - Hours(3), topic: "Ski", subject: "Ski trip", imid: "<s1@x>");
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - Hours(3), AiOutcome.Drafted, null, 1, 0, 0.9, "D0",
            Now - Hours(3) + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - Hours(3) + TimeSpan.FromMinutes(5), FP, Now - Hours(3));
        h.Seed("N1", at: Now - TimeSpan.FromMinutes(30), topic: "Ski", subject: "Re: Ski trip",
            sender: "bob@contoso.example", to: "Ann <ann@contoso.example>", body: "N1-BODY");
        h.Mailbox.DraftInfo = (id, _) => id.StartsWith('D') ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        return h;
    }

    // 3
    [Fact]
    public async Task Revisit_uses_the_Revisit_kind()
    {
        using var h = FxRevisit();
        h.Model.Enqueue(Keep());
        await h.Worker.RunPassAsync(Ct);
        Assert.Equal(ModelCallKind.Revisit, Assert.Single(h.Model.Requests).Kind);
    }

    // 4
    [Fact]
    public async Task Rescan_marks_its_emails_Rescan_and_its_revisit_Revisit()
    {
        using var h = new DraftHarness();
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Ai.MeetingTimes = false;
        h.Ai.RequireDirectRecipient = true;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        h.Seed("A", at: Now - Hours(10), topic: "Alpha", subject: "Alpha");
        h.Seed("S1", at: Now - Hours(20), topic: "Ski", subject: "Ski trip", imid: "<s1@x>");
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - Hours(20), AiOutcome.Drafted, null, 1, 0, 0.9,
            "D0", Now - Hours(20) + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - Hours(20) + TimeSpan.FromMinutes(5), FP, Now - Hours(20));
        h.Seed("N1", at: Now - Hours(2), topic: "Ski", subject: "Re: Ski trip", sender: "bob@contoso.example",
            to: "List <list@contoso.example>", body: "N1-BODY");
        h.Seed("B", at: Now - Hours(1), topic: "Beta", subject: "Beta");
        h.Mailbox.DraftInfo = (id, _) => id.StartsWith('D') ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Model.OnRun = (req, _) => Task.FromResult(req.JsonSchema == DraftModel.RevisitSchema ? Keep() : OkReply(0.9, needsReply: false));

        await h.Worker.RescanAsync(Ct);

        Assert.Equal([ModelCallKind.Rescan, ModelCallKind.Revisit, ModelCallKind.Rescan], h.Model.Requests.Select(r => r.Kind));
    }

    // 5
    [Fact]
    public async Task Pass_log_line_sums_in_and_out_and_names_unknown_calls()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(10));
        h.Seed("C2", age: TimeSpan.FromMinutes(9));
        h.Model.Enqueue(WithUsage(OkReply(0.9, needsReply: false), new TokenUsage(100, 20, 300, 0)),
            new ModelResult.Failed(ModelFailure.Timeout, null, null));

        await h.Worker.RunPassAsync(Ct);

        Assert.Equal(2, h.Model.Requests.Count);
        var line = Assert.Single(h.Log.Lines, l => l.StartsWith("AI drafts pass:", StringComparison.Ordinal));
        Assert.EndsWith(" ms, tokens in 400 out 20, 1 calls without token data", line);
    }

    // 6
    [Fact]
    public async Task Rate_limited_pass_reports_zero_tokens()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(new ModelResult.RateLimited(null));

        await h.Worker.RunPassAsync(Ct);

        var line = Assert.Single(h.Log.Lines, l => l.StartsWith("AI drafts pass:", StringComparison.Ordinal));
        Assert.EndsWith(" ms, tokens in 0 out 0", line);
    }

    // 7 is the exact-line assertion in DraftWorkerRescanTests.

    // 8
    [Fact]
    public async Task No_log_line_or_event_holds_prompt_text_subject_or_address()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));

        await h.Worker.RunPassAsync(Ct);

        foreach (var text in h.AllText())
        {
            Assert.DoesNotContain("BODYTEXT-123", text);
            Assert.DoesNotContain("ann@contoso.example", text);
        }
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("Budget", StringComparison.Ordinal));
    }
}
