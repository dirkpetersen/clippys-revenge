using System.Text.Json;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerRescanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;
    private static TimeSpan Hours(double h) => TimeSpan.FromHours(h);

    private static DraftHarness Fx()
    {
        var h = new DraftHarness();
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Ai.MeetingTimes = false;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        return h;
    }

    private static Task<RescanResult> Run(DraftHarness h) => h.Worker.RescanAsync(Ct);

    private static string[] AiTexts(DraftHarness h) => h.Events(ActivityKind.Ai).Select(e => e.Summary).ToArray();

    private static ModelResult R(string action, double conf, string? body = null)
    {
        var d = new Dictionary<string, object?> { ["action"] = action, ["confidence"] = conf, ["reason"] = "SECRET-REASON" };
        if (body is not null) d["body_html"] = body;
        return Ok(JsonSerializer.Serialize(d));
    }

    /// <summary>S1 (Now-20h, topic Ski, drafted as D0, tracked) and N1 (Now-2h, bob, to a list: index rules skip it).</summary>
    private static DraftHarness FxRevisit(bool requireDirect = true)
    {
        var h = Fx();
        h.Ai.RequireDirectRecipient = requireDirect;
        h.Seed("S1", at: Now - Hours(20), topic: "Ski", subject: "Ski trip", imid: "<s1@x>");
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - Hours(20), AiOutcome.Drafted, null, 1, 0, 0.9,
            "D0", Now - Hours(20) + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - Hours(20) + TimeSpan.FromMinutes(5), FP, Now - Hours(20));
        h.Seed("N1", at: Now - Hours(2), topic: "Ski", subject: "Re: Ski trip", sender: "bob@contoso.example",
            to: "List <list@contoso.example>", body: "N1-BODY");
        h.Mailbox.DraftInfo = (id, _) => id.StartsWith('D') ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        return h;
    }

    // ---------------------------------------------------------------- plan

    [Fact]
    public void Plan_counts_the_rows_the_rescan_would_go_through()
    {
        using var h = Fx();
        var idx = h.Indexes.For("");
        h.Seed("A", at: Now - Hours(23));
        h.Seed("B", at: Now - Hours(25));
        h.Seed("C", at: Now - Hours(2), sender: "noreply@contoso.example");
        h.Seed("D", folder: "Inbox\\Sub", at: Now - Hours(3));
        h.Seed("E", at: Now - Hours(4));
        idx.UpsertDecision(new AiDecision("E", null, null, Now, AiOutcome.Drafted, null, 1, 0, 0.9, "DX", Now));
        h.Seed("F", at: Now - Hours(5));
        idx.UpsertDecision(new AiDecision("F", null, null, Now, AiOutcome.NoReply, null, 1, 0, 0.9));
        h.Seed("G", at: Now - Hours(1), sender: "me@example.org");
        h.Seed("H", at: Now - Hours(6));
        idx.UpsertDecision(new AiDecision("H", null, null, Now, AiOutcome.Failed, null, 1, 3));

        var plan = h.Worker.PlanRescan();

        Assert.Equal(3, plan.Emails);
        Assert.Equal(0, h.Mailbox.TotalCalls);
        Assert.Empty(h.Model.Requests);
        Assert.Equal(AiOutcome.Failed, h.Decision("H")!.Outcome);
        Assert.Equal(AiOutcome.Drafted, h.Decision("E")!.Outcome);
    }

    [Fact]
    public void Plan_ignores_the_enable_time_and_the_enabled_switch()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        Assert.Equal(1, h.Worker.PlanRescan().Emails);
        h.Ai.Enabled = false;
        Assert.Equal(1, h.Worker.PlanRescan().Emails);
    }

    [Fact]
    public void Plan_counts_shared_mailboxes_with_ai_drafts_on()
    {
        using var h = Fx();
        h.Seed("S", mailbox: DraftHarness.Shared, to: "Help <helpdesk@example.org>", at: Now - Hours(2));
        Assert.Equal(0, h.Worker.PlanRescan().Emails);
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        Assert.Equal(1, h.Worker.PlanRescan().Emails);
    }

    [Fact]
    public void Plan_cap_and_calls_left()
    {
        using var h = Fx();
        h.Ai.DailyCap = 3;
        var idx = h.Indexes.For("");
        idx.UpsertDecision(new AiDecision("X", null, null, Now, AiOutcome.NoReply, null, 2, 0, 0.9));
        var plan = h.Worker.PlanRescan();
        Assert.Equal(1, plan.CallsLeft);
        Assert.Equal(3, plan.Cap);
        Assert.False(plan.LimitReached);

        idx.UpsertDecision(new AiDecision("X", null, null, Now, AiOutcome.NoReply, null, 3, 0, 0.9));
        plan = h.Worker.PlanRescan();
        Assert.Equal(0, plan.CallsLeft);
        Assert.True(plan.LimitReached);
    }

    [Fact]
    public void Plan_confirm_text_follows_revise_drafts()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        var plan = h.Worker.PlanRescan();
        Assert.Equal(RescanTexts.Confirm(1, plan.CallsLeft, plan.Cap, true), plan.ConfirmText);
        Assert.Contains("Outdated Clippy drafts", plan.ConfirmText);
        h.Ai.ReviseDrafts = false;
        plan = h.Worker.PlanRescan();
        Assert.DoesNotContain("Outdated Clippy drafts", plan.ConfirmText);
    }

    [Fact]
    public void Plan_counts_a_revisit_due_email_despite_the_index_rules()
    {
        using var h = FxRevisit();
        Assert.Equal(1, h.Worker.PlanRescan().Emails);

        h.Ai.ReviseDrafts = false;
        Assert.Equal(0, h.Worker.PlanRescan().Emails);

        h.Ai.ReviseDrafts = true;
        h.Indexes.For("").SetAiDraftState("D0", AiDraftState.Open, Now - Hours(2), 0, Now);
        Assert.Equal(0, h.Worker.PlanRescan().Emails);
    }

    [Fact]
    public void Plan_without_the_own_address_skips_the_index_rules()
    {
        using var h = new DraftHarness();
        h.Seed("A", at: Now - Hours(23));
        h.Seed("B", at: Now - Hours(25));
        h.Seed("C", at: Now - Hours(2), sender: "noreply@contoso.example");
        h.Seed("F", at: Now - Hours(5));
        h.Indexes.For("").UpsertDecision(new AiDecision("F", null, null, Now, AiOutcome.NoReply, null, 1, 0, 0.9));
        h.Seed("G", at: Now - Hours(1), sender: "me@example.org");
        h.Seed("H", at: Now - Hours(6));
        h.Seed("E", at: Now - Hours(4));
        h.Indexes.For("").UpsertDecision(new AiDecision("E", null, null, Now, AiOutcome.Drafted, null, 1, 0, 0.9, "DX", Now));

        Assert.Equal(5, h.Worker.PlanRescan().Emails);
    }

    // ---------------------------------------------------------------- run

    [Fact]
    public async Task Run_goes_oldest_first_over_own_and_shared_with_interactive_priority()
    {
        using var h = Fx();
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("A", at: Now - Hours(3));
        h.Seed("B", at: Now - Hours(10));
        h.Seed("C", at: Now - Hours(1));
        h.Seed("S", mailbox: DraftHarness.Shared, to: "Help <helpdesk@example.org>", at: Now - Hours(5));
        h.Model.OnRun = (_, _) => Task.FromResult(OkReply(0.9, needsReply: false));

        var r = await Run(h);

        Assert.Equal([("", "B"), (DraftHarness.Shared, "S"), ("", "A"), ("", "C")],
            h.Mailbox.FactCalls.Select(x => (x.Mailbox, x.EntryId)));
        Assert.All(h.Mailbox.FactCalls, x => Assert.Equal(WorkPriority.Interactive, x.Priority));
        Assert.Equal("Rescan: 4 emails, 0 drafts, 4 no reply needed (0 s)", r.Message);
        Assert.True(r.Started);
        Assert.Null(r.Stop);
        Assert.Equal(4, r.Processed);
        Assert.Equal(4, r.ModelCalls);
        Assert.Equal("Rescan: 4 emails, 0 drafts, 4 no reply needed (0 s)", Assert.Single(AiTexts(h)));
        Assert.Contains(h.Log.Lines,
            l => l == "AI drafts rescan: Completed, 4 emails, 0 drafted, 0 updated, 0 moved, 0 kept, 4 no reply, 0 low confidence, 0 skipped, 0 failed, 4 calls in 0 ms");
    }

    [Fact]
    public async Task Run_creates_and_tracks_a_draft()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(2), topic: "Ski", subject: "Ski");
        h.Model.Enqueue(OkReply(0.9));

        var r = await Run(h);

        var plan = Assert.Single(h.Writer.Plans);
        Assert.Equal("ai", plan.Marker);
        Assert.Equal(WorkPriority.Interactive, plan.Priority);
        var d = h.Decision("A")!;
        Assert.Equal(AiOutcome.Drafted, d.Outcome);
        Assert.Equal(1, d.ModelCalls);
        var tracked = h.Indexes.For("").GetAiDraft("D2")!;
        Assert.Equal(AiDraftState.Open, tracked.State);
        Assert.Equal("A", tracked.SourceEntryId);
        Assert.Equal(Now - Hours(2), tracked.Checked);
        Assert.Equal(DraftTexts.DraftCreated("Re:: Ski"), Assert.Single(h.Events(ActivityKind.Draft)).Summary);
        Assert.Equal("Rescan: 1 email, 1 draft (0 s)", r.Message);
        Assert.True(r.ShowDrafts);
        Assert.Empty(h.Shown);
    }

    [Fact]
    public async Task Run_ignores_a_decided_once_row()
    {
        using var h = Fx();
        h.Seed("F", at: Now - Hours(5));
        h.Indexes.For("").UpsertDecision(new AiDecision("F", null, null, Now - Hours(4), AiOutcome.NoReply, null, 1, 0, 0.9));
        h.Model.Enqueue(OkReply(0.9, needsReply: false));

        await Run(h);

        var d = h.Decision("F")!;
        Assert.Equal(AiOutcome.NoReply, d.Outcome);
        Assert.Equal(2, d.ModelCalls);
    }

    [Fact]
    public async Task Run_draft_exists_skips_without_a_call()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(2));
        h.Mailbox.Facts = (_, _) => FakeAiMailbox.DefaultFacts with { DraftExists = true };

        var r = await Run(h);

        Assert.Empty(h.Model.Requests);
        var d = h.Decision("A")!;
        Assert.Equal(AiOutcome.Skipped, d.Outcome);
        Assert.Equal("draft_exists", d.Rule);
        Assert.Equal("Rescan: 1 email, 0 drafts, 1 skipped (0 s)", r.Message);
    }

    [Fact]
    public async Task Run_skips_a_row_drafted_during_the_run()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(3));
        h.Seed("B", at: Now - Hours(2));
        h.Model.OnRun = (_, n) =>
        {
            h.Indexes.For("").UpsertDecision(new AiDecision("B", null, null, Now, AiOutcome.Drafted, null, 1, 0, 0.9, "DY", Now));
            return Task.FromResult(OkReply(0.9, needsReply: false));
        };

        var r = await Run(h);

        Assert.Single(h.Model.Requests);
        Assert.Equal("Rescan: 2 emails, 0 drafts, 1 no reply needed, 1 skipped (0 s)", r.Message);
    }

    [Fact]
    public async Task Run_works_with_drafting_switched_off()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(2));
        h.Ai.Enabled = false;
        h.Model.Enqueue(OkReply(0.9, needsReply: false));

        var r = await Run(h);

        Assert.True(r.Started);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task Run_needs_consent()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(2));
        h.Ai.ConsentedAtUtc = null;

        var r = await Run(h);

        Assert.False(r.Started);
        Assert.Equal(AiDraftingSettings.ConsentRequiredMessage, r.Message);
        Assert.Equal(0, h.Mailbox.TotalCalls);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(AiTexts(h));
    }

    [Fact]
    public async Task Run_stops_at_the_daily_limit()
    {
        using var h = Fx();
        h.Ai.DailyCap = 2;
        h.Seed("A", at: Now - Hours(4));
        h.Seed("B", at: Now - Hours(3));
        h.Seed("C", at: Now - Hours(2));
        h.Model.OnRun = (_, _) => Task.FromResult(OkReply(0.9));

        var r = await Run(h);

        Assert.Equal(2, h.Writer.Plans.Count);
        Assert.Equal(RescanStop.DailyLimit, r.Stop);
        Assert.Equal(2, r.Processed);
        Assert.Equal("Rescan stopped (daily limit of 2 reached) after 2 of 3 emails: 2 drafts (0 s)", r.Message);
        Assert.Contains(h.Log.Lines, l => l.StartsWith("AI drafts rescan: DailyLimit, 3 emails, ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_stops_at_the_rate_limit_and_the_pass_then_backs_off()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        h.Seed("B", at: Now - Hours(4));
        h.Seed("C", at: Now - TimeSpan.FromMinutes(10));
        h.Model.Enqueue(OkReply(0.9, needsReply: false), new ModelResult.RateLimited(Now + TimeSpan.FromMinutes(30)));

        var r = await Run(h);

        Assert.Equal(RescanStop.RateLimited, r.Stop);
        Assert.Equal("Rescan stopped (Claude's usage limit was reached) after 1 of 3 emails: 0 drafts, 1 no reply needed (0 s)",
            r.Message);
        Assert.Null(h.Decision("B"));
        Assert.Contains(AiTexts(h), t => t.StartsWith("AI drafts: Claude's usage limit was reached.", StringComparison.Ordinal));
        Assert.Equal(DraftPassEnd.Backoff, (await h.Worker.RunPassAsync(Ct)).End);
    }

    [Fact]
    public async Task Run_ignores_the_backoff_of_a_failed_pass()
    {
        using var h = Fx();
        h.Seed("P", at: Now - TimeSpan.FromMinutes(10));
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null), OkReply(0.9));
        Assert.Equal(DraftPassEnd.Failed, (await h.Worker.RunPassAsync(Ct)).End);

        var r = await Run(h);

        Assert.Equal(2, h.Model.Requests.Count);
        Assert.Equal(AiOutcome.Drafted, h.Decision("P")!.Outcome);
        Assert.Equal("Rescan: 1 email, 1 draft (0 s)", r.Message);
    }

    [Fact]
    public async Task Run_stops_after_three_failed_calls_in_a_row()
    {
        using var h = Fx();
        for (var i = 1; i <= 4; i++) h.Seed("E" + i, at: Now - Hours(10 - i));
        h.Model.OnRun = (_, _) => Task.FromResult<ModelResult>(new ModelResult.Failed(ModelFailure.Timeout, null));

        var r = await Run(h);

        Assert.Equal(3, h.Model.Requests.Count);
        Assert.Equal(RescanStop.Failures, r.Stop);
        Assert.Equal("Rescan stopped (3 Claude calls failed in a row) after 3 of 4 emails: 0 drafts, 3 failed (0 s)", r.Message);
        Assert.DoesNotContain(AiTexts(h), t => t.Contains("a Claude call failed", StringComparison.Ordinal));
        Assert.NotEqual(DraftPassEnd.Backoff, (await h.Worker.RunPassAsync(Ct)).End);
    }

    [Fact]
    public async Task Run_resets_the_failure_streak_on_a_parsed_result()
    {
        using var h = Fx();
        for (var i = 1; i <= 5; i++) h.Seed("E" + i, at: Now - Hours(10 - i));
        var fail = new ModelResult.Failed(ModelFailure.Timeout, null);
        h.Model.Enqueue(fail, OkReply(0.9, needsReply: false), fail, fail, OkReply(0.9, needsReply: false));

        var r = await Run(h);

        Assert.Null(r.Stop);
        Assert.Equal("Rescan: 5 emails, 0 drafts, 2 no reply needed, 3 failed (0 s)", r.Message);
    }

    [Fact]
    public async Task Run_counts_a_com_facts_failure_as_failed_but_not_as_a_call_failure()
    {
        using var h = Fx();
        for (var i = 1; i <= 3; i++) h.Seed("E" + i, at: Now - Hours(10 - i));
        h.Mailbox.FactsThrow = new InvalidOperationException();

        var r = await Run(h);

        Assert.Empty(h.Model.Requests);
        Assert.Null(r.Stop);
        Assert.Equal("Rescan: 3 emails, 0 drafts, 3 failed (0 s)", r.Message);
    }

    [Fact]
    public async Task Run_stops_when_not_signed_in_and_a_later_ok_releases_the_hold()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        h.Seed("B", at: Now - Hours(4));
        h.Model.Enqueue(new ModelResult.NotSignedIn());

        var r = await Run(h);

        Assert.Equal(RescanStop.NotSignedIn, r.Stop);
        Assert.StartsWith("Rescan stopped (Claude isn't signed in) after 0 of 2 emails: ", r.Message);
        Assert.Single(h.Events(ActivityKind.Error), e => e.Summary == DraftTexts.NotSignedInScope);
        Assert.Equal(ActivityState.Error, h.Hub.State);

        h.Model.Enqueue(OkReply(0.9, needsReply: false), OkReply(0.9, needsReply: false));
        var r2 = await Run(h);

        Assert.Null(r2.Stop);
        Assert.NotEqual(ActivityState.Error, h.Hub.State);
    }

    [Fact]
    public async Task Run_stops_at_once_when_outlook_is_unavailable()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        h.Available = false;

        var r = await Run(h);

        Assert.Equal(RescanStop.OutlookUnavailable, r.Stop);
        Assert.Equal("Rescan stopped (Outlook isn't running) after 0 of 0 emails: 0 drafts (0 s)", r.Message);
        Assert.Equal(0, h.Mailbox.TotalCalls);

        h.Available = true;
        h.Mailbox.IdentityThrow = new OutlookUnavailableException();
        Assert.Equal(RescanStop.OutlookUnavailable, (await Run(h)).Stop);
    }

    [Fact]
    public async Task Run_stops_when_the_own_address_is_unknown()
    {
        using var h = new DraftHarness();
        h.Seed("A", at: Now - Hours(5));
        h.Mailbox.Identity = new AiIdentity("Dirk", "");

        var r = await Run(h);

        Assert.Equal(RescanStop.OutlookUnavailable, r.Stop);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: the own mailbox address is unknown; rescan skipped.");
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task Cancel_between_emails_stops_after_the_current_one()
    {
        using var h = Fx();
        for (var i = 1; i <= 3; i++) h.Seed("E" + i, at: Now - Hours(10 - i));
        h.Model.OnRun = (_, n) =>
        {
            if (n == 1) h.Worker.CancelRescan();
            return Task.FromResult(OkReply(0.9, needsReply: false));
        };

        var r = await Run(h);

        Assert.Single(h.Model.Requests);
        Assert.Equal(RescanStop.Cancelled, r.Stop);
        Assert.Equal("Rescan stopped (cancelled) after 1 of 3 emails: 0 drafts, 1 no reply needed (0 s)", r.Message);
    }

    [Fact]
    public async Task Cancel_during_a_call_writes_no_row()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        h.Seed("B", at: Now - Hours(4));
        h.Model.OnRun = (_, _) =>
        {
            h.Worker.CancelRescan();
            return Task.FromResult<ModelResult>(new ModelResult.Failed(ModelFailure.Cancelled, null));
        };

        var r = await Run(h);

        Assert.Null(h.Decision("A"));
        Assert.Equal(RescanStop.Cancelled, r.Stop);
        Assert.Contains("after 0 of 2 emails", r.Message);
    }

    [Fact]
    public async Task An_ok_result_finishes_the_email_before_the_cancel_is_seen()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        h.Seed("B", at: Now - Hours(4));
        h.Model.OnRun = (_, _) =>
        {
            h.Worker.CancelRescan();
            return Task.FromResult(OkReply(0.9));
        };

        var r = await Run(h);

        Assert.Single(h.Writer.Plans);
        Assert.Equal(AiOutcome.Drafted, h.Decision("A")!.Outcome);
        Assert.NotNull(h.Indexes.For("").GetAiDraft("D2"));
        Assert.Equal("Rescan stopped (cancelled) after 1 of 2 emails: 1 draft (0 s)", r.Message);
    }

    [Fact]
    public async Task Progress_is_reported_while_running()
    {
        using var h = Fx();
        for (var i = 1; i <= 3; i++) h.Seed("E" + i, at: Now - Hours(10 - i));
        RescanProgress? seen = null;
        ActivityProgress hub = default;
        h.Model.OnRun = (_, n) =>
        {
            if (n == 2)
            {
                seen = h.Worker.CurrentRescan;
                hub = h.Hub.Progress;
            }
            return Task.FromResult(OkReply(0.9, needsReply: false));
        };

        await Run(h);

        Assert.Equal(new RescanProgress(1, 3), seen);
        Assert.Equal(new ActivityProgress(1, 3, "Rescanning the last 24 hours"), hub);
        Assert.Null(h.Worker.CurrentRescan);
    }

    // ---------------------------------------------------------------- revisits

    [Fact]
    public async Task Later_thread_mail_revisits_the_draft_and_keep_marks_it()
    {
        using var h = FxRevisit();
        h.Ai.Enabled = false;
        h.Model.Enqueue(R("keep", 0.9));

        var r = await Run(h);

        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(DraftModel.RevisitSchema, req.JsonSchema);
        Assert.All(h.Mailbox.DraftReads, x => Assert.Equal(WorkPriority.Interactive, x.Priority));
        Assert.Equal([("D0", "keep", 1)], h.RevisitRows());
        var n1 = h.Decision("N1")!;
        Assert.Equal(AiOutcome.Skipped, n1.Outcome);
        Assert.Equal("revisited", n1.Rule);
        Assert.Equal("Rescan: 1 email, 0 drafts, 1 draft still fine (0 s)", r.Message);
    }

    [Fact]
    public async Task Revisit_replace_saves_first_then_moves()
    {
        using var h = FxRevisit(requireDirect: false);
        var movesWhenCreated = -1;
        h.Writer.Behavior = (_, _) =>
        {
            movesWhenCreated = h.Mailbox.Moves.Count;
            return new RepliedDraft("D2", "Re:: Ski", "", FP2);
        };
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        var r = await Run(h);

        Assert.Equal(0, movesWhenCreated);
        Assert.Equal(WorkPriority.Interactive, Assert.Single(h.Writer.Plans).Priority);
        Assert.Equal([("", "D0", FP, WorkPriority.Interactive)], h.Mailbox.Moves);
        Assert.Equal("Rescan: 1 email, 0 drafts, 1 draft updated (0 s)", r.Message);
        Assert.True(r.ShowDrafts);
    }

    [Fact]
    public async Task Revisit_remove_moves_the_old_draft()
    {
        using var h = FxRevisit(requireDirect: false);
        h.Model.Enqueue(R("remove", 0.9));

        var r = await Run(h);

        Assert.Equal("Rescan: 1 email, 0 drafts, 1 outdated draft moved to Deleted Items (0 s)", r.Message);
        Assert.Empty(h.Writer.Plans);
    }

    [Fact]
    public async Task Two_later_emails_make_two_revisits()
    {
        using var h = FxRevisit(requireDirect: false);
        h.Seed("N2", at: Now - Hours(1), topic: "Ski", sender: "bob@contoso.example", to: "List <list@contoso.example>");
        h.Model.Enqueue(R("keep", 0.9), R("keep", 0.9));

        await Run(h);

        Assert.Equal(2, h.Model.Requests.Count);
        Assert.Equal(Now - Hours(1), h.Indexes.For("").GetAiDraft("D0")!.Checked);
    }

    [Fact]
    public async Task Revisit_without_a_call_falls_through_when_the_draft_is_gone_and_skips_when_still_open()
    {
        using var h = FxRevisit();
        h.Mailbox.DraftInfo = (_, _) => null;

        var r = await Run(h);

        Assert.Equal(AiDraftState.Gone, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Empty(h.Model.Requests);
        var d = h.Decision("N1")!;
        Assert.Equal(AiOutcome.Skipped, d.Outcome);
        Assert.Equal("not_direct", d.Rule);
        Assert.Equal("Rescan: 1 email, 0 drafts, 1 skipped (0 s)", r.Message);

        using var h2 = FxRevisit();
        h2.Mailbox.DraftInfo = (_, _) => FakeAiMailbox.Draft(FP) with { Open = true };

        var r2 = await Run(h2);

        Assert.Equal(AiDraftState.Open, h2.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Null(h2.Decision("N1"));
        Assert.Equal("Rescan: 1 email, 0 drafts, 1 skipped (0 s)", r2.Message);
    }

    [Fact]
    public async Task Revise_drafts_off_runs_the_candidate_steps()
    {
        using var h = FxRevisit(requireDirect: false);
        h.Ai.ReviseDrafts = false;
        h.Model.Enqueue(OkReply(0.9, needsReply: false));

        await Run(h);

        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(DraftModel.ReplySchema, req.JsonSchema);
        Assert.Empty(h.Mailbox.DraftReads);
    }

    [Fact]
    public async Task Revisit_ignores_the_backoff_and_a_failure_is_not_a_backoff()
    {
        using var h = FxRevisit();
        h.Seed("P", at: Now - TimeSpan.FromMinutes(10), topic: "Other");
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));
        Assert.Equal(DraftPassEnd.Failed, (await h.Worker.RunPassAsync(Ct)).End);
        var callsBefore = h.Model.Requests.Count;
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null), new ModelResult.Failed(ModelFailure.Timeout, null));

        var r = await Run(h);

        // The revisit and the failed pass row P both call although the pass left a backoff.
        Assert.Equal(callsBefore + 2, h.Model.Requests.Count);
        Assert.Contains("2 failed", r.Message);
    }

    [Fact]
    public async Task Revisit_failure_in_a_rescan_counts_an_attempt_without_an_event()
    {
        using var h = FxRevisit();
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));

        var r = await Run(h);

        Assert.Equal([("D0", "failed", 1)], h.RevisitRows());
        Assert.Equal(1, h.Indexes.For("").GetAiDraft("D0")!.Attempts);
        Assert.DoesNotContain(AiTexts(h), t => t.Contains("a Claude call failed", StringComparison.Ordinal));
        Assert.Equal("Rescan: 1 email, 0 drafts, 1 failed (0 s)", r.Message);
    }

    // ---------------------------------------------------------------- concurrency and privacy

    [Fact]
    public async Task A_second_rescan_returns_at_once_and_a_pass_waits_for_the_first()
    {
        using var h = Fx();
        h.Seed("A", at: Now - Hours(5));
        h.Seed("P", at: Now - TimeSpan.FromMinutes(10), topic: "Other");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Model.OnRun = async (_, n) =>
        {
            if (n == 1)
            {
                entered.SetResult();
                await release.Task;
            }
            return OkReply(0.9, needsReply: false);
        };

        var first = Run(h);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var second = await Run(h);
        Assert.False(second.Started);
        Assert.Equal("", second.Message);
        Assert.Single(h.Model.Requests);

        var pass = h.Worker.RunPassAsync(Ct);
        await Task.Delay(100, Ct);
        Assert.False(pass.IsCompleted);

        release.SetResult();
        var r = await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await pass.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(r.Started);
    }

    [Fact]
    public async Task Logs_and_events_hold_no_mail_text()
    {
        using var h = FxRevisit(requireDirect: false);
        h.Seed("A", at: Now - Hours(1), topic: "Budget", subject: "Re:: Ski");
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"), OkReply(0.9));

        await Run(h);

        foreach (var bad in new[] { "Re:: Ski", "BODYTEXT-123", "OLD-DRAFT-TEXT", "D0", "D2", "SECRET-REASON", "bob@" })
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains(bad, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Hub.GetEvents(), e => e.Summary.Contains("SECRET-REASON", StringComparison.Ordinal));
    }

[Fact]
    public async Task Rescan_prompts_carry_the_facts()
    {
        using var h = Fx();
        h.Facts.Text = "FACTS-MARKER-42 x";
        h.Seed("P", at: Now - TimeSpan.FromMinutes(10));
        h.Model.Enqueue(OkReply(0.9));
        await Run(h);
        Assert.Contains("<about_me>\nFACTS-MARKER-42 x\n</about_me>", Assert.Single(h.Model.Requests).SystemPrompt);

        using var h2 = FxRevisit(requireDirect: false);
        h2.Facts.Text = "FACTS-MARKER-42 x";
        h2.Model.Enqueue(R("keep", 0.9));
        await Run(h2);
        Assert.Contains("<about_me>\nFACTS-MARKER-42 x\n</about_me>", h2.Model.Requests[0].SystemPrompt);
        Assert.DoesNotContain(h2.AllText(), t => t.Contains("FACTS-MARKER-42", StringComparison.Ordinal));
    }
}
