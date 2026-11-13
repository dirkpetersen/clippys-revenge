using ModelContextProtocol;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerManualTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DraftHarness Make()
    {
        var h = new DraftHarness();
        h.Ai.Enabled = false;   // the manual path doesn't need it
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1",
            Folder = "Inbox",
            Subject = "Budget",
            SenderEmail = "ann@contoso.example",
            To = "Me <me@example.org>",
            ConversationTopic = "Budget",
            Body = "BODYTEXT-123",
            EventTime = DraftHarness.Now.AddMinutes(-5),
        });
        return h;
    }

    private static string Failed(string why) => DraftTexts.ManualFailed(why);

    [Fact]
    public async Task Happy_path_even_when_the_model_says_no_reply_needed()
    {
        using var h = Make();
        h.Model.Enqueue(OkReply(0.1, needsReply: false, body: "<p>Sure</p>"));
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(new ManualDraftResult(true, "It looks like you want a reply. It's in your Drafts folder."), r);
        var plan = Assert.Single(h.Writer.Plans);
        Assert.Equal("ai", plan.Marker);
        Assert.Equal(WorkPriority.Interactive, plan.Priority);
        Assert.Equal("E1", plan.Id);
        Assert.Contains("asked for a reply to this email", Assert.Single(h.Model.Requests).SystemPrompt);
        var d = h.Decision("E1")!;
        Assert.Equal((AiOutcome.Drafted, 1), (d.Outcome, d.ModelCalls));
        Assert.Empty(h.Mailbox.FactCalls);
        Assert.Single(h.Events(ActivityKind.Draft));
    }

    [Fact]
    public async Task No_consent()
    {
        using var h = Make();
        h.Ai.ConsentedAtUtc = null;
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(new ManualDraftResult(false, AiDraftingSettings.ConsentRequiredMessage), r);
        Assert.Equal(0, h.Mailbox.TotalCalls);
    }

    [Fact]
    public async Task No_selection()
    {
        using var h = Make();
        h.Mailbox.Selected = null;
        Assert.Equal(DraftTexts.ManualNoSelection, (await h.Worker.DraftSelectedAsync(Ct)).Message);
    }

    [Fact]
    public async Task Email_not_in_a_mailbox()
    {
        using var h = Make();
        h.Mailbox.Live = null;
        Assert.Equal(DraftTexts.ManualNotInMailbox, (await h.Worker.DraftSelectedAsync(Ct)).Message);
    }

    [Fact]
    public async Task Outlook_not_running()
    {
        using var h = Make();
        h.Mailbox.SelectedThrow = new OutlookUnavailableException();
        Assert.Equal("I couldn't draft that reply: Outlook isn't running", (await h.Worker.DraftSelectedAsync(Ct)).Message);
    }

    [Fact]
    public async Task Model_not_ready()
    {
        using var h = Make();
        h.Model.Status = new ModelStatus(false, "✗ Claude Code wasn't found. Install it.");
        Assert.Equal("Claude isn't ready: ✗ Claude Code wasn't found. Install it.", (await h.Worker.DraftSelectedAsync(Ct)).Message);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task Cap_reached()
    {
        using var h = Make();
        h.Ai.DailyCap = 1;
        h.Indexes.For("").UpsertDecision(new AiDecision("X", null, null, DraftHarness.Now.AddMinutes(-1), AiOutcome.NoReply, ModelCalls: 1));
        Assert.Equal("Today's AI draft limit (1) is used up.", (await h.Worker.DraftSelectedAsync(Ct)).Message);
    }

    [Fact]
    public async Task Shared_mailbox_without_an_open_index_fails_instead_of_using_the_own_index()
    {
        using var h = Make();
        h.Mailbox.Live = h.Mailbox.Live! with { Mailbox = "shared-not-indexed@example.org" };
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(new ManualDraftResult(false, DraftTexts.ManualNotIndexed), r);
        Assert.Empty(h.Writer.Plans);
        Assert.Null(h.Decision("E1"));
    }
    [Fact]
    public async Task Rate_limited_sets_backoff_for_automatic_passes_only()
    {
        using var h = Make();
        h.Model.Enqueue(new ModelResult.RateLimited(null));
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(Failed("Claude's usage limit was reached"), r.Message);
        Assert.Null(h.Decision("E1"));

        h.Ai.SetEnabled(true, DraftHarness.Now.AddHours(-1));
        h.Seed("C1");
        Assert.Equal(DraftPassEnd.Backoff, (await h.Worker.RunPassAsync(Ct)).End);
    }

    [Fact]
    public async Task Not_signed_in_holds_the_scope()
    {
        using var h = Make();
        h.Model.Enqueue(new ModelResult.NotSignedIn());
        Assert.Equal(Failed("Claude isn't signed in"), (await h.Worker.DraftSelectedAsync(Ct)).Message);
        Assert.Equal(ActivityState.Error, h.Hub.State);
        Assert.Null(h.Decision("E1"));
    }

    [Fact]
    public async Task Call_failure_writes_a_failed_row_without_backoff()
    {
        using var h = Make();
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.ProcessExit, 1));
        Assert.Equal(Failed("the Claude call failed (ProcessExit)"), (await h.Worker.DraftSelectedAsync(Ct)).Message);
        var d = h.Decision("E1")!;
        Assert.Equal((AiOutcome.Failed, 1, 1), (d.Outcome, d.Attempts, d.ModelCalls));
        h.Model.Enqueue(OkReply(0.9));
        Assert.True((await h.Worker.DraftSelectedAsync(Ct)).Drafted);
    }

    [Fact]
    public async Task Blank_body_is_counted()
    {
        using var h = Make();
        h.Model.Enqueue(OkReply(0.9, needsReply: false, body: ""));
        Assert.Equal(Failed("Claude didn't write a reply"), (await h.Worker.DraftSelectedAsync(Ct)).Message);
        Assert.Equal(1, h.Decision("E1")!.ModelCalls);
    }

    [Fact]
    public async Task Save_failure()
    {
        using var h = Make();
        h.Model.Enqueue(OkReply(0.9));
        h.Writer.Behavior = (_, _) => throw new McpException("x");
        Assert.Equal(Failed("the draft couldn't be saved in Outlook"), (await h.Worker.DraftSelectedAsync(Ct)).Message);
        Assert.Equal(1, h.Decision("E1")!.ModelCalls);
    }

    [Fact]
    public async Task Email_gone_when_saving()
    {
        using var h = Make();
        h.Model.Enqueue(OkReply(0.9));
        h.Writer.Behavior = (_, _) => throw new ItemNotFoundException("E1");
        Assert.Equal(Failed("that email is no longer in Outlook"), (await h.Worker.DraftSelectedAsync(Ct)).Message);
    }

    [Fact]
    public async Task Prior_row_keeps_its_outcome_and_gains_a_call()
    {
        using var h = Make();
        h.Indexes.For("").UpsertDecision(new AiDecision("E1", null, null, DraftHarness.Now.AddMinutes(-3), AiOutcome.NoReply, ModelCalls: 1, Confidence: 0.9));
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));
        await h.Worker.DraftSelectedAsync(Ct);
        var d = h.Decision("E1")!;
        Assert.Equal((AiOutcome.NoReply, 2), (d.Outcome, d.ModelCalls));
    }

    [Fact]
    public async Task Not_indexed_email_counts_toward_the_cap()
    {
        using var h = Make();
        h.Model.Enqueue(OkReply(0.9));
        Assert.True((await h.Worker.DraftSelectedAsync(Ct)).Drafted);
        Assert.Equal(1, h.Worker.ModelCallsToday());
    }

    [Fact]
    public async Task Success_ends_a_hold_and_ignores_backoff()
    {
        using var h = Make();
        h.Model.Enqueue(new ModelResult.NotSignedIn());
        await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(ActivityState.Error, h.Hub.State);
        h.Model.Enqueue(OkReply(0.9));
        Assert.True((await h.Worker.DraftSelectedAsync(Ct)).Drafted);
        Assert.NotEqual(ActivityState.Error, h.Hub.State);

        h.Model.Enqueue(new ModelResult.RateLimited(null), OkReply(0.9));
        await h.Worker.DraftSelectedAsync(Ct);
        Assert.True((await h.Worker.DraftSelectedAsync(Ct)).Drafted);
    }
}
