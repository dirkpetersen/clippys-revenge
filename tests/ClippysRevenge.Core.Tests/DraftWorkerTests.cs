using ModelContextProtocol;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<DraftPassResult> Pass(DraftHarness h, CancellationToken? ct = null) => h.Worker.RunPassAsync(ct ?? Ct);

    private static Func<ModelRequest, int, Task<ModelResult>> Always(ModelResult r) => (_, _) => Task.FromResult(r);

    private static string[] AiTexts(DraftHarness h) => h.Events(ActivityKind.Ai).Select(e => e.Summary).ToArray();

    // 1-3
    [Fact]
    public async Task Happy_path_creates_an_ai_draft()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.Completed, r.End);
        Assert.Equal(1, r.Drafted);
        var plan = Assert.Single(h.Writer.Plans);
        Assert.Equal("C1", plan.Id);
        Assert.Null(plan.Mailbox);
        Assert.False(plan.ReplyAll);
        Assert.Equal("ai", plan.Marker);
        Assert.Equal(WorkPriority.Background, plan.Priority);
        Assert.Contains("<p>Hi <span style=\"background-color:#ffff00\">[[date]]</span></p>", plan.HtmlFragment);
        Assert.DoesNotContain("<script", plan.HtmlFragment);
        var d = h.Decision("C1")!;
        Assert.Equal(AiOutcome.Drafted, d.Outcome);
        Assert.Equal(1, d.ModelCalls);
        Assert.Equal(0, d.Attempts);
        Assert.Equal(0.9, d.Confidence);
        Assert.Equal("D1", d.DraftEntryId);
        Assert.Equal(DraftHarness.Now, d.DraftCreated);
        Assert.Null(d.ConversationTopic);
        Assert.Equal("AI reply draft created: Re:: Budget", Assert.Single(h.Events(ActivityKind.Draft)).Summary);
        Assert.StartsWith("AI drafts: 1 drafted (", Assert.Single(AiTexts(h)));
        Assert.Equal(BalloonTexts.AiDrafted(1), Assert.Single(h.Shown).Text);
    }

    [Fact]
    public async Task Reply_all_is_passed_on()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9, replyAll: true));
        await Pass(h);
        Assert.True(Assert.Single(h.Writer.Plans).ReplyAll);
    }

    [Fact]
    public async Task Request_carries_settings_prompts_and_schema()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(DraftModel.ReplySchema, req.JsonSchema);
        Assert.Equal(h.Ai.Model, req.Model);
        Assert.Equal(h.Ai.Effort, req.Effort);
        Assert.Equal(TimeSpan.FromSeconds(180), req.Timeout);
        Assert.Equal(8000, req.MaxOutputTokens);
        Assert.Contains("Dirk Example <me@example.org>", req.SystemPrompt);
        Assert.EndsWith("<writing_style>\nSTYLE\n</writing_style>", req.SystemPrompt);
        Assert.Contains("mailbox: own", req.UserPrompt);
        Assert.Contains("attachments=\"a.pdf\"", req.UserPrompt);
        Assert.Contains("BODYTEXT-123", req.UserPrompt);
        Assert.Equal(1, h.Style.BeforeCalls);
    }

    // 4-5
    [Fact]
    public async Task Candidate_window_respects_enable_time_and_age()
    {
        using var h = new DraftHarness();
        h.Seed("OLD1", at: h.Ai.EnabledAtUtc!.Value.AddSeconds(-1));
        h.Seed("NEW1", at: h.Ai.EnabledAtUtc!.Value.AddSeconds(1));
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        Assert.Single(h.Model.Requests);
        Assert.Null(h.Decision("OLD1"));
        Assert.Equal(AiOutcome.Drafted, h.Decision("NEW1")!.Outcome);
    }

    [Fact]
    public async Task Rows_older_than_max_age_are_not_candidates()
    {
        using var h = new DraftHarness();
        h.Ai.EnabledAtUtc = DraftHarness.Now.AddDays(-5);
        h.Seed("OLD", at: DraftHarness.Now.AddDays(-3));
        var r = await Pass(h);
        Assert.Equal(0, r.ModelCalls);
        Assert.Empty(h.Model.Requests);
        Assert.Null(h.Decision("OLD"));
    }

    [Fact]
    public async Task Only_the_inbox_itself_is_scanned()
    {
        using var h = new DraftHarness();
        h.Seed("S1", folder: @"Inbox\Sub");
        h.Seed("S2", folder: "Sent Items");
        await Pass(h);
        Assert.Empty(h.Model.Requests);
        Assert.Empty(h.Mailbox.FactCalls);
    }

    // 6-8
    [Fact]
    public async Task Index_rule_skips_without_com_or_model()
    {
        using var h = new DraftHarness();
        h.Seed("C1", sender: "noreply@x.org");
        var r = await Pass(h);
        Assert.Equal(1, r.Skipped);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Skipped, "no_reply"), (d.Outcome, d.Rule));
        Assert.Empty(h.Mailbox.FactCalls);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task Replied_in_sent_items_skips()
    {
        using var h = new DraftHarness();
        h.Seed("C1", imid: "<src@x>");
        h.Seed("SENT1", folder: "Sent Items", irt: "<src@x>", sender: "me@example.org", topic: "Other");
        await Pass(h);
        Assert.Equal("replied", h.Decision("C1")!.Rule);
    }

    [Fact]
    public async Task Shared_candidate_replied_via_own_sent_items()
    {
        using var h = new DraftHarness();
        h.Ai.EnabledAtUtc = DraftHarness.Now.AddHours(-1);
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("S1", mailbox: DraftHarness.Shared, imid: "<s1@x>", to: "Help <helpdesk@example.org>");
        h.Seed("SENT1", folder: "Sent Items", irt: "<s1@x>", sender: "me@example.org", topic: "Other");
        await Pass(h);
        var d = h.Decision("S1", DraftHarness.Shared)!;
        Assert.Equal((AiOutcome.Skipped, "replied"), (d.Outcome, d.Rule));
    }

    // 9-11
    [Fact]
    public async Task Com_rule_skips_and_uses_background_priority()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Mailbox.Facts = (_, _) => new CandidateFacts("IPM.Schedule.Meeting.Request", "<i@x>", "Budget", null, null, false);
        await Pass(h);
        Assert.Equal("not_note", h.Decision("C1")!.Rule);
        Assert.Empty(h.Model.Requests);
        Assert.All(h.Mailbox.FactCalls, c => Assert.Equal(WorkPriority.Background, c.Priority));
    }

    [Fact]
    public async Task Gone_item_is_skipped()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Mailbox.Facts = (_, _) => null;
        await Pass(h);
        Assert.Equal("gone", h.Decision("C1")!.Rule);
    }

    [Fact]
    public async Task Outlook_unavailable_while_reading_facts_ends_the_pass_quietly()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Mailbox.FactsThrow = new OutlookUnavailableException();
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.OutlookUnavailable, r.End);
        Assert.Null(h.Decision("C1"));
        Assert.Empty(AiTexts(h));
    }

    // 12-13
    [Fact]
    public async Task No_reply_needed()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.95, needsReply: false));
        var r = await Pass(h);
        Assert.Equal(1, r.NoReply);
        var d = h.Decision("C1")!;
        Assert.Equal(AiOutcome.NoReply, d.Outcome);
        Assert.Equal(0.95, d.Confidence);
        Assert.Empty(h.Writer.Plans);
        Assert.StartsWith("AI drafts: 0 drafted, 1 no reply needed (", Assert.Single(AiTexts(h)));
        Assert.Empty(h.Shown);
    }

    [Fact]
    public async Task Low_confidence_is_not_drafted()
    {
        using var h = new DraftHarness();
        h.Ai.MinConfidence = 0.6;
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.5));
        var r = await Pass(h);
        Assert.Equal(AiOutcome.LowConfidence, h.Decision("C1")!.Outcome);
        Assert.Empty(h.Writer.Plans);
        Assert.Equal(1, r.NoReply);
    }

    // 14-19
    [Fact]
    public async Task Invalid_output_is_a_bad_output_failure()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(Ok("""{"needs_reply":true,"confidence":2}"""));
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Failed, r.End);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Failed, 1, 1), (d.Outcome, d.Attempts, d.ModelCalls));
        Assert.Contains("AI drafts: a Claude call failed (BadOutput). I'll try again in 1 min.", AiTexts(h));
    }

    [Fact]
    public async Task Body_that_sanitizes_to_nothing_is_a_bad_output_failure()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9, body: "<script>x</script>"));
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Failed, r.End);
        Assert.Contains("AI drafts: a Claude call failed (BadOutput). I'll try again in 1 min.", AiTexts(h));
    }

    [Fact]
    public async Task Failures_back_off_and_stop_after_three_attempts()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.OnRun = Always(new ModelResult.Failed(ModelFailure.Timeout, null));
        foreach (var (wait, min) in new[] { (2, 1), (3, 2), (5, 4) })
        {
            var r = await Pass(h);
            Assert.Equal(DraftPassEnd.Failed, r.End);
            Assert.Contains($"AI drafts: a Claude call failed (Timeout). I'll try again in {min} min.", AiTexts(h));
            h.Time.Advance(TimeSpan.FromMinutes(wait));
        }
        Assert.Equal(3, h.Decision("C1")!.Attempts);
        var last = await Pass(h);
        Assert.Equal(DraftPassEnd.Completed, last.End);
        Assert.Equal(3, h.Model.Requests.Count);
    }

    [Fact]
    public async Task Backoff_blocks_calls_but_still_runs_rules()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(10));
        h.Model.Enqueue(Ok("""{"needs_reply":true,"confidence":2}"""));
        await Pass(h);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        h.Seed("C2", age: TimeSpan.FromMinutes(1));
        var factsBefore = h.Mailbox.FactCalls.Count;

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.Backoff, r.End);
        Assert.Single(h.Model.Requests);
        Assert.True(h.Mailbox.FactCalls.Count > factsBefore);
        Assert.Null(h.Decision("C2"));

        h.Model.Enqueue(OkReply(0.9));
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await Pass(h);
        Assert.True(h.Model.Requests.Count >= 2);
    }

    [Fact]
    public async Task Failure_backoff_sequence()
    {
        using var h = new DraftHarness();
        h.Ai.DailyCap = 500;
        h.Seed("C1", age: TimeSpan.FromMinutes(3));
        h.Seed("C2", age: TimeSpan.FromMinutes(4));
        h.Seed("C3", age: TimeSpan.FromMinutes(5));
        h.Model.OnRun = Always(new ModelResult.Failed(ModelFailure.ProcessExit, 1));
        for (var i = 0; i < 8; i++)
        {
            await Pass(h);
            h.Time.Advance(TimeSpan.FromMinutes(61));
        }
        var minutes = AiTexts(h).Where(t => t.Contains("call failed", StringComparison.Ordinal)).Select(t => int.Parse(System.Text.RegularExpressions.Regex.Match(t, @"in (\d+) min").Groups[1].Value)).ToArray();
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], minutes);
    }

    [Fact]
    public async Task Ok_resets_the_failure_counter()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(3));
        h.Seed("C2", age: TimeSpan.FromMinutes(4));
        h.Model.Enqueue(Ok("""{"needs_reply":true,"confidence":2}"""));
        await Pass(h);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Model.Enqueue(OkReply(0.9, needsReply: false), Ok("""{"needs_reply":true,"confidence":2}"""));
        await Pass(h);
        var failedTexts = AiTexts(h).Where(t => t.Contains("call failed", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, failedTexts.Length);
        Assert.All(failedTexts, t => Assert.Contains("in 1 min", t));
    }

    // 20-24
    [Fact]
    public async Task Rate_limit_with_a_time_waits_until_then()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(new ModelResult.RateLimited(DraftHarness.Now.AddMinutes(30)));
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.RateLimited, r.End);
        Assert.Null(h.Decision("C1"));
        Assert.Contains("AI drafts: Claude's usage limit was reached. I'll try again at 12:31.", AiTexts(h));
        h.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(DraftPassEnd.Backoff, (await Pass(h)).End);
        Assert.Single(h.Model.Requests);
        h.Model.Enqueue(OkReply(0.9));
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DraftPassEnd.Completed, (await Pass(h)).End);
        Assert.Equal(2, h.Model.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_without_a_time_waits_an_hour()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(new ModelResult.RateLimited(null));
        await Pass(h);
        Assert.Contains("AI drafts: Claude's usage limit was reached. I'll try again at 13:00.", AiTexts(h));
    }

    [Fact]
    public async Task Not_signed_in_holds_until_settings_change()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(new ModelResult.NotSignedIn(), OkReply(0.9));
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.NotSignedIn, r.End);
        Assert.Null(h.Decision("C1"));
        Assert.Equal(ActivityState.Error, h.Hub.State);
        Assert.Equal(DraftTexts.NotSignedInScope, h.Hub.Progress.Label);
        Assert.Single(h.Events(ActivityKind.Error));

        var factsBefore = h.Mailbox.FactCalls.Count;
        Assert.Equal(DraftPassEnd.NotSignedIn, (await Pass(h)).End);
        Assert.Equal(factsBefore, h.Mailbox.FactCalls.Count);
        Assert.Single(h.Model.Requests);
        Assert.Single(h.Events(ActivityKind.Error));

        h.Worker.NotifySettingsChanged();
        Assert.Equal(DraftPassEnd.Completed, (await Pass(h)).End);
        Assert.Equal(2, h.Model.Requests.Count);
        Assert.NotEqual(ActivityState.Error, h.Hub.State);
    }

    [Fact]
    public async Task Report_status_ready_ends_the_hold_but_not_ready_does_not()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(new ModelResult.NotSignedIn());
        await Pass(h);
        h.Worker.ReportStatus(new ModelStatus(false, "✗"));
        Assert.Equal(DraftPassEnd.NotSignedIn, (await Pass(h)).End);
        h.Worker.ReportStatus(new ModelStatus(true, "✓"));
        Assert.NotEqual(DraftPassEnd.NotSignedIn, (await Pass(h)).End);
    }

    [Fact]
    public async Task Turning_drafting_off_ends_the_hold()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(new ModelResult.NotSignedIn());
        await Pass(h);
        h.Ai.Enabled = false;
        Assert.Equal(DraftPassEnd.Disabled, (await Pass(h)).End);
        Assert.NotEqual(ActivityState.Error, h.Hub.State);
    }

    // 25-26
    private static void SeedCalls(DraftHarness h, string mailbox, string id, int calls, DateTimeOffset decided) =>
        h.Indexes.For(mailbox).UpsertDecision(new AiDecision(id, null, null, decided, AiOutcome.NoReply, ModelCalls: calls));

    [Fact]
    public async Task Daily_limit_counts_all_indexes_and_reports_once()
    {
        using var h = new DraftHarness();
        h.Ai.DailyCap = 2;
        SeedCalls(h, "", "X1", 1, DraftHarness.Now.AddHours(-1));
        SeedCalls(h, DraftHarness.Shared, "X2", 1, DraftHarness.Now.AddHours(-1));
        h.Seed("C1");
        Assert.Equal(DraftPassEnd.DailyLimit, (await Pass(h)).End);
        Assert.Equal(DraftPassEnd.DailyLimit, (await Pass(h)).End);
        Assert.Empty(h.Model.Requests);
        Assert.Single(AiTexts(h), "AI drafts: daily limit of 2 reached. I'll continue tomorrow.");
    }

    [Fact]
    public async Task Calls_before_local_midnight_do_not_count_and_the_event_repeats_next_day()
    {
        using var h = new DraftHarness();
        h.Ai.DailyCap = 2;
        h.Ai.MaxAgeDays = 3;
        // Local midnight is 08:00Z; 07:59Z is 23:59 the day before.
        SeedCalls(h, "", "X1", 2, DateTimeOffset.Parse("2026-11-13T07:59:00Z"));
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));
        Assert.Equal(DraftPassEnd.Completed, (await Pass(h)).End);
        Assert.Single(h.Model.Requests);

        h.Time.Advance(TimeSpan.FromHours(13));
        SeedCalls(h, "", "X3", 2, h.Time.GetUtcNow());
        h.Seed("C2");
        Assert.Equal(DraftPassEnd.DailyLimit, (await Pass(h)).End);
        h.Time.Advance(TimeSpan.FromHours(24));
        SeedCalls(h, "", "X4", 2, h.Time.GetUtcNow());
        h.Seed("C3");
        Assert.Equal(DraftPassEnd.DailyLimit, (await Pass(h)).End);
        Assert.Equal(2, AiTexts(h).Count(t => t.StartsWith("AI drafts: daily limit", StringComparison.Ordinal)));
    }

    // 27-30
    [Theory]
    [InlineData("replied")]
    [InlineData("draft_exists")]
    [InlineData("gone")]
    public async Task Recheck_before_create_skips(string rule)
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Mailbox.Facts = (_, n) => n == 1
            ? FakeAiMailbox.DefaultFacts
            : rule switch
            {
                "replied" => FakeAiMailbox.DefaultFacts with { LastVerb = 102 },
                "draft_exists" => FakeAiMailbox.DefaultFacts with { DraftExists = true },
                _ => null,
            };
        h.Model.Enqueue(OkReply(0.8));
        await Pass(h);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Skipped, rule, 1), (d.Outcome, d.Rule, d.ModelCalls));
        Assert.Equal(0.8, d.Confidence);
        Assert.Empty(h.Writer.Plans);
    }

    [Fact]
    public async Task Writer_item_not_found_is_skipped_gone()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));
        h.Writer.Behavior = (_, _) => throw new ItemNotFoundException("C1");
        await Pass(h);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Skipped, "gone", 1), (d.Outcome, d.Rule, d.ModelCalls));
    }

    [Fact]
    public async Task Save_failure_is_recorded_and_the_pass_goes_on_without_leaking_the_message()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(1));
        h.Seed("C2", age: TimeSpan.FromMinutes(2));
        h.Model.Enqueue(OkReply(0.9), OkReply(0.9));
        h.Writer.Behavior = (_, n) => n == 1 ? throw new McpException("SECRET-X") : new RepliedDraft("D2", "Re:: Budget", "");
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Completed, r.End);
        Assert.Equal((1, 1), (r.Drafted, r.Failed));
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Failed, 1), (d.Outcome, d.Attempts));
        Assert.Contains(DraftTexts.SaveFailed, AiTexts(h));
        Assert.DoesNotContain(h.AllText(), t => t.Contains("SECRET-X", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outlook_unavailable_while_creating_writes_a_failed_row()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));
        h.Writer.Behavior = (_, _) => throw new OutlookUnavailableException();
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.OutlookUnavailable, r.End);
        Assert.Equal(AiOutcome.Failed, h.Decision("C1")!.Outcome);
    }

    // 31-32
    [Fact]
    public async Task Gates()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Ai.Enabled = false;
        Assert.Equal(DraftPassEnd.Disabled, (await Pass(h)).End);
        h.Ai.Enabled = true;
        h.Ai.ConsentedAtUtc = null;
        Assert.Equal(DraftPassEnd.Disabled, (await Pass(h)).End);
        h.Ai.RecordConsent(DraftHarness.Now.AddHours(-2));
        h.Paused = true;
        Assert.Equal(DraftPassEnd.Paused, (await Pass(h)).End);
        h.Paused = false;
        h.Available = false;
        Assert.Equal(DraftPassEnd.OutlookUnavailable, (await Pass(h)).End);
        Assert.Equal(0, h.Mailbox.TotalCalls);
        Assert.Equal(0, h.Style.BeforeCalls);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task Unknown_identity_skips_the_pass_unless_the_index_knows_the_address()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Mailbox.Identity = new AiIdentity("", "");
        Assert.Equal(DraftPassEnd.NoIdentity, (await Pass(h)).End);
        h.Indexes.For("").SetMeta("mailbox_smtp", "me@example.org");
        h.Model.Enqueue(OkReply(0.9));
        Assert.Equal(DraftPassEnd.Completed, (await Pass(h)).End);
        Assert.Single(h.Writer.Plans);
    }

    // 33-35
    [Fact]
    public async Task Shared_mailboxes_need_the_ai_drafts_opt_in()
    {
        using var h = new DraftHarness();
        h.Seed("S1", mailbox: DraftHarness.Shared, to: "Help <helpdesk@example.org>");
        await Pass(h);
        Assert.Empty(h.Mailbox.FactCalls);

        h.Settings.SharedMailboxes[0].Enabled = false;
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        await Pass(h);
        Assert.Empty(h.Mailbox.FactCalls);

        h.Settings.SharedMailboxes[0].Enabled = true;
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        Assert.Equal(DraftHarness.Shared, Assert.Single(h.Mailbox.FactCalls.Select(c => c.Mailbox).Distinct()));
        Assert.Contains("mailbox: shared helpdesk@example.org", Assert.Single(h.Model.Requests).UserPrompt);
    }

    [Fact]
    public async Task Shared_mailbox_uses_its_own_address_for_the_self_rule()
    {
        using var h = new DraftHarness();
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("S1", mailbox: DraftHarness.Shared, sender: DraftHarness.Shared, to: "Help <helpdesk@example.org>");
        await Pass(h);
        Assert.Equal("self", h.Decision("S1", DraftHarness.Shared)!.Rule);
    }

    [Fact]
    public async Task At_most_fifty_candidates_per_mailbox()
    {
        using var h = new DraftHarness();
        h.Ai.DailyCap = 500;
        for (var i = 0; i < 60; i++) h.Seed("C" + i, age: TimeSpan.FromMinutes(1 + i));
        h.Model.OnRun = Always(OkReply(0.9, needsReply: false));
        await Pass(h);
        Assert.Equal(50, Enumerable.Range(0, 60).Count(i => h.Decision("C" + i) is not null));
    }

    [Fact]
    public async Task Prior_failure_counts_carry_over()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Indexes.For("").UpsertDecision(new AiDecision("C1", null, null, DraftHarness.Now, AiOutcome.Failed, null, 1, 1));
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Drafted, 2, 1), (d.Outcome, d.ModelCalls, d.Attempts));
    }

    // 36-39
    [Fact]
    public async Task Settings_are_re_read_between_candidates()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(1));
        h.Seed("C2", age: TimeSpan.FromMinutes(2));
        h.Model.OnRun = (_, _) =>
        {
            h.Ai.Enabled = false;
            return Task.FromResult(OkReply(0.9));
        };
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Disabled, r.End);
        Assert.NotNull(h.Decision("C1"));
        Assert.Null(h.Decision("C2"));
    }

    [Fact]
    public async Task Drafting_scope_is_active_during_the_call_only()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        (ActivityState, ActivityProgress) seen = default;
        h.Model.OnRun = (_, _) =>
        {
            seen = (h.Hub.State, h.Hub.Progress);
            return Task.FromResult(OkReply(0.9));
        };
        await Pass(h);
        Assert.Equal(ActivityState.Drafting, seen.Item1);
        Assert.Equal(new ActivityProgress(0, 1, "drafting replies"), seen.Item2);
        Assert.Equal(ActivityState.Idle, h.Hub.State);
    }

    [Fact]
    public async Task Summary_with_mixed_outcomes()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(1));
        h.Seed("C2", age: TimeSpan.FromMinutes(2));
        h.Seed("C3", age: TimeSpan.FromMinutes(3), sender: "noreply@x.org");
        h.Seed("C4", age: TimeSpan.FromMinutes(4), sender: "noreply@x.org");
        h.Seed("C5", age: TimeSpan.FromMinutes(5));
        h.Model.Enqueue(OkReply(0.9), OkReply(0.9, needsReply: false), Ok("""{"needs_reply":true,"confidence":2}"""));
        await Pass(h);
        Assert.StartsWith("AI drafts: 1 drafted, 1 no reply needed, 2 skipped, 1 failed (",
            AiTexts(h).Single(t => t.Contains("drafted", StringComparison.Ordinal) && t.Contains('(')));
    }

    [Fact]
    public async Task Empty_pass_is_silent()
    {
        using var h = new DraftHarness();
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Completed, r.End);
        Assert.Empty(AiTexts(h));
        Assert.Empty(h.Shown);
    }

    // 40
    [Fact]
    public async Task Logs_and_events_hold_no_mail_text()
    {
        using var h = new DraftHarness();
        h.Seed("C1", age: TimeSpan.FromMinutes(1));
        h.Seed("C2", age: TimeSpan.FromMinutes(2));
        h.Seed("C3", age: TimeSpan.FromMinutes(3));
        h.Seed("C4", age: TimeSpan.FromMinutes(4));
        h.Model.Enqueue(OkReply(0.9), OkReply(0.9, needsReply: false), Ok("""{"needs_reply":true,"confidence":2}"""));
        await Pass(h);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Model.Enqueue(OkReply(0.9), OkReply(0.9, needsReply: false));
        h.Writer.Behavior = (_, n) => n == 1 ? new RepliedDraft("D1", "Re:: Budget", "") : throw new McpException("SECRET-X");
        await Pass(h);

        string[] forbidden = ["BODYTEXT-123", "SECRET-REASON", "SECRET-X", "ann@", "contoso", "STYLE", "You draft email replies"];
        foreach (var t in h.AllText())
            foreach (var f in forbidden) Assert.DoesNotContain(f, t, StringComparison.Ordinal);
        foreach (var e in h.Hub.GetEvents().Where(e => e.Kind != ActivityKind.Draft))
            Assert.DoesNotContain("Budget", e.Summary, StringComparison.Ordinal);
        foreach (var t in h.Log.Lines) Assert.DoesNotContain("Budget", t, StringComparison.Ordinal);
        foreach (var id in new[] { "C1", "C2", "C3", "C4" })
            if (h.Decision(id) is { } d) Assert.Null(d.ConversationTopic);
    }

    // 41-45
    [Fact]
    public async Task Own_cancellation_writes_no_row()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        using var cts = new CancellationTokenSource();
        h.Model.OnRun = (_, _) =>
        {
            cts.Cancel();
            return Task.FromResult<ModelResult>(new ModelResult.Failed(ModelFailure.Cancelled, null));
        };
        var r = await Pass(h, cts.Token);
        Assert.Equal(DraftPassEnd.Cancelled, r.End);
        Assert.Null(h.Decision("C1"));
    }

    [Fact]
    public async Task A_manual_draft_makes_a_running_pass_yield_at_the_next_candidate()
    {
        using var h = new DraftHarness();
        h.Ai.DailyCap = 500;
        h.Seed("C1", age: TimeSpan.FromMinutes(1));
        h.Seed("C2", age: TimeSpan.FromMinutes(2));
        h.Seed("C3", age: TimeSpan.FromMinutes(3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Model.OnRun = async (_, n) =>
        {
            if (n == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return OkReply(0.9);
        };
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail { EntryId = "E1", Folder = "Inbox", EventTime = DraftHarness.Now.AddMinutes(-5), Body = "hi" });

        var pass = Pass(h);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var manual = h.Worker.DraftSelectedAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.Worker.YieldRequests == 0 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        Assert.True(h.Worker.YieldRequests > 0);
        release.SetResult();

        var r = await pass.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(DraftPassEnd.Yielded, r.End);
        Assert.NotNull(h.Decision("C1"));
        Assert.Null(h.Decision("C2"));
        Assert.Null(h.Decision("C3"));
        Assert.True((await manual.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Drafted);
    }

    [Fact]
    public async Task A_failing_style_hook_is_logged_and_ignored()
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Style.ThrowBefore = new IOException("SECRET-X");
        h.Model.Enqueue(OkReply(0.9));
        var r = await Pass(h);
        Assert.Equal(1, r.Drafted);
        Assert.Contains(h.Log.Lines, l => l.Contains("IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Log.Lines, l => l.Contains("SECRET-X", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_calls_today_sums_all_indexes()
    {
        using var h = new DraftHarness();
        Assert.Equal(0, h.Worker.ModelCallsToday());
        SeedCalls(h, "", "X1", 1, DraftHarness.Now.AddMinutes(-5));
        SeedCalls(h, DraftHarness.Shared, "X2", 2, DraftHarness.Now.AddMinutes(-5));
        Assert.Equal(3, h.Worker.ModelCallsToday());
    }

    [Fact]
    public async Task The_loop_runs_a_pass_on_wake_and_every_fifteen_minutes()
    {
        using var h = new DraftHarness();
        h.Ai.DailyCap = 500;
        h.Seed("C1");
        h.Model.OnRun = Always(OkReply(0.9, needsReply: false));
        await h.Worker.StartAsync(Ct);
        try
        {
            await WaitFor(() => h.Worker.WaitCount >= 1);
            Assert.Empty(h.Model.Requests);
            h.Worker.Wake();
            await WaitFor(() => h.Model.Requests.Count == 1 && h.Worker.WaitCount >= 2);

            h.Seed("C2", age: TimeSpan.FromMinutes(1));
            h.Time.Advance(TimeSpan.FromMinutes(15));
            await WaitFor(() => h.Model.Requests.Count == 2);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await h.Worker.StopAsync(stop.Token);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10, Ct);
        }
    }
}
