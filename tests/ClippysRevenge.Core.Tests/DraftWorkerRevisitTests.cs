using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerRevisitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string FP = new('a', 64);
    private static readonly string FP2 = new('c', 64);
    private static DateTimeOffset Now => DraftHarness.Now;

    private static readonly TimeSpan H3 = TimeSpan.FromHours(3);

    private static Task<DraftPassResult> Pass(DraftHarness h) => h.Worker.RunPassAsync(Ct);

    private static string[] AiTexts(DraftHarness h) => h.Events(ActivityKind.Ai).Select(e => e.Summary).ToArray();

    private static string[] DraftTexts_(DraftHarness h) => h.Events(ActivityKind.Draft).Select(e => e.Summary).ToArray();

    /// <summary>The revisit answer, with the model's "reason" set to a canary that must never surface.</summary>
    private static ModelResult R(string action, double conf, string? body = null, bool? replyAll = null, string? meeting = null)
    {
        var d = new Dictionary<string, object?> { ["action"] = action, ["confidence"] = conf, ["reason"] = "SECRET-REASON" };
        if (body is not null) d["body_html"] = body;
        if (replyAll is not null) d["reply_all"] = replyAll;
        if (meeting is not null) d["meeting"] = JsonDocument.Parse(meeting).RootElement.Clone();
        return Ok(JsonSerializer.Serialize(d));
    }

    private static DateTimeOffset N1Time(TimeSpan ago) => Now - ago;

    /// <summary>S1 (drafted as D0) plus, by default, a newer N1 from bob 30 minutes ago (a candidate too, unless the
    /// revisit marks it).</summary>
    private static DraftHarness Fx(bool n1 = true, TimeSpan? n1Ago = null)
    {
        var h = new DraftHarness();
        h.Ai.MeetingTimes = false;
        h.Ai.RequireDirectRecipient = false;
        h.Seed("S1", at: Now - H3, topic: "Ski", subject: "Ski trip", imid: "<s1@x>");
        h.Indexes.For("").UpsertDecision(new AiDecision("S1", "<s1@x>", null, Now - H3, AiOutcome.Drafted, null, 1, 0, 0.9, "D0",
            Now - H3 + TimeSpan.FromMinutes(5)));
        h.Track("D0", "S1", Now - H3 + TimeSpan.FromMinutes(5), FP, Now - H3);
        if (n1)
            h.Seed("N1", at: Now - (n1Ago ?? TimeSpan.FromMinutes(30)), topic: "Ski", subject: "Re: Ski trip",
                sender: "bob@contoso.example", to: "Ann <ann@contoso.example>", body: "N1-BODY");
        h.Mailbox.DraftInfo = (id, _) => id.StartsWith('D') ? FakeAiMailbox.Draft(FP, "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Writer.Behavior = (_, _) => new RepliedDraft("D2", "Re:: Ski", "", FP2);
        return h;
    }

    private static readonly TimeSpan Old = TimeSpan.FromMinutes(90);   // before the enable time: never a candidate

    // 1
    [Fact]
    public async Task Keep_makes_one_call_and_marks_the_newer_mail()
    {
        using var h = Fx();
        h.Model.Enqueue(R("keep", 0.9));

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.Completed, r.End);
        var req = Assert.Single(h.Model.Requests);
        Assert.Equal(DraftModel.RevisitSchema, req.JsonSchema);
        Assert.Equal(8000, req.MaxOutputTokens);
        Assert.Equal(DraftPrompt.RevisitSystemPrompt("Dirk Example", "me@example.org", "STYLE", false), req.SystemPrompt);
        Assert.Contains("<current_draft created=\"", req.UserPrompt);
        Assert.Contains("OLD-DRAFT-TEXT", req.UserPrompt);
        Assert.Contains("N1-BODY", req.UserPrompt);
        Assert.Equal([("D0", "keep", 1)], h.RevisitRows());
        var d = h.Indexes.For("").GetAiDraft("D0")!;
        Assert.Equal(AiDraftState.Open, d.State);
        Assert.Equal(N1Time(TimeSpan.FromMinutes(30)), d.Checked);
        Assert.Equal(0, d.Attempts);
        Assert.Empty(h.Writer.Plans);
        Assert.Empty(h.Mailbox.Moves);
        var n1 = h.Decision("N1")!;
        Assert.Equal(AiOutcome.Skipped, n1.Outcome);
        Assert.Equal("revisited", n1.Rule);
        Assert.Equal(1, r.Kept);
        Assert.Equal(1, r.ModelCalls);
        Assert.StartsWith("AI drafts: 0 drafted, 1 kept (", Assert.Single(AiTexts(h)));
        Assert.Empty(h.Shown);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: revisit keep");
    }

    // 2
    [Fact]
    public async Task Revisits_count_toward_the_daily_cap()
    {
        using var h = Fx();
        h.Model.Enqueue(R("keep", 0.9));
        await Pass(h);
        Assert.Equal(2, h.Worker.ModelCallsToday());
    }

    [Fact]
    public async Task Second_revisit_stops_at_the_cap_after_its_read()
    {
        using var h = Fx();
        h.Seed("SX", at: Now - H3, topic: "Other");
        h.Track("DX", "SX", Now - H3, FP, Now - H3);
        h.Seed("NX", at: Now - TimeSpan.FromMinutes(10), topic: "Other");
        h.Ai.DailyCap = 2;
        h.Model.Enqueue(R("keep", 0.9));

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.DailyLimit, r.End);
        Assert.Single(h.Model.Requests);
        Assert.Equal(["DX", "D0"], h.Mailbox.DraftReads.Select(x => x.EntryId));
        Assert.Contains(AiTexts(h), t => t == DraftTexts.DailyLimit(2));
    }

    // 3
    [Theory]
    [InlineData("replace", 0.3)]
    [InlineData("remove", 0.5)]
    public async Task Low_confidence_replace_or_remove_keeps_the_draft(string action, double conf)
    {
        using var h = Fx();
        h.Model.Enqueue(R(action, conf, "<p>x</p>"));

        var r = await Pass(h);

        Assert.Equal([("D0", "low_confidence", 1)], h.RevisitRows());
        Assert.Equal(N1Time(TimeSpan.FromMinutes(30)), h.Indexes.For("").GetAiDraft("D0")!.Checked);
        Assert.Empty(h.Writer.Plans);
        Assert.Empty(h.Mailbox.Moves);
        Assert.DoesNotContain(AiTexts(h), t => t.StartsWith("AI drafts: no draft for the email", StringComparison.Ordinal));
        Assert.Equal(1, r.Kept);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: revisit low_confidence");
    }

    [Fact]
    public async Task Keep_is_a_keep_at_any_confidence()
    {
        using var h = Fx();
        h.Model.Enqueue(R("keep", 0.1));
        var r = await Pass(h);
        Assert.Equal([("D0", "keep", 1)], h.RevisitRows());
        Assert.Equal(1, r.Kept);
    }

    // 4, 25, 26
    [Fact]
    public async Task Replace_saves_the_new_draft_before_moving_the_old_one()
    {
        using var h = Fx();
        var movesWhenCreated = -1;
        h.Writer.Behavior = (_, _) =>
        {
            movesWhenCreated = h.Mailbox.Moves.Count;
            return new RepliedDraft("D2", "Re:: Ski", "", FP2);
        };
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p><script>x</script>", replyAll: true));

        var r = await Pass(h);

        Assert.Equal(0, movesWhenCreated);
        var plan = Assert.Single(h.Writer.Plans);
        Assert.Equal("N1", plan.Id);
        Assert.True(plan.ReplyAll);
        Assert.Equal("ai", plan.Marker);
        Assert.Equal(WorkPriority.Background, plan.Priority);
        Assert.Contains("<p>New</p>", plan.HtmlFragment);
        Assert.DoesNotContain("<script", plan.HtmlFragment);
        Assert.Equal([("", "D0", FP, WorkPriority.Background)], h.Mailbox.Moves);
        var idx = h.Indexes.For("");
        Assert.Equal(AiDraftState.Replaced, idx.GetAiDraft("D0")!.State);
        var d2 = idx.GetAiDraft("D2")!;
        Assert.Equal("N1", d2.SourceEntryId);
        Assert.Equal(AiDraftState.Open, d2.State);
        Assert.Equal(FP2, d2.Fingerprint);
        Assert.Equal(N1Time(TimeSpan.FromMinutes(30)), d2.Checked);
        var n1 = h.Decision("N1")!;
        Assert.Equal(AiOutcome.Drafted, n1.Outcome);
        Assert.Equal("D2", n1.DraftEntryId);
        Assert.Equal(0, n1.ModelCalls);
        Assert.Equal(0.9, n1.Confidence);
        Assert.Equal([("D0", "replace", 1)], h.RevisitRows());
        Assert.Equal("AI reply draft updated (the thread moved on): Re:: Ski. The old draft is in Deleted Items.",
            Assert.Single(DraftTexts_(h)));
        Assert.Equal(1, r.Updated);
        Assert.Equal(0, r.Drafted);
        Assert.StartsWith("AI drafts: 0 drafted, 1 updated (", Assert.Single(AiTexts(h)));
        var balloon = Assert.Single(h.Shown);
        Assert.Equal(BalloonTexts.AiDrafted(1), balloon.Text);
        Assert.Equal(BalloonAction.ShowDrafts, balloon.Action);
        Assert.Single(h.Model.Requests);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: revisit replaced");
        Assert.Contains(h.Log.Lines, l => l.Contains("0 drafted, 1 updated, 0 moved, 0 kept", StringComparison.Ordinal));

        // 25: nothing private in logs or events.
        foreach (var bad in new[] { "Re:: Ski", "OLD-DRAFT-TEXT", "N1-BODY", "D0", "D2", FP, FP2, "SECRET-REASON", "bob@" })
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains(bad, StringComparison.Ordinal));
        Assert.DoesNotContain(h.Hub.GetEvents(), e => e.Summary.Contains("SECRET-REASON", StringComparison.Ordinal));
    }

    // 5
    [Fact]
    public async Task Replace_answers_the_newest_message_the_rules_accept()
    {
        using var h = Fx();
        h.Seed("N2", at: Now - TimeSpan.FromMinutes(20), topic: "Ski", sender: "noreply@contoso.example");
        h.Seed("N3", at: Now - TimeSpan.FromMinutes(10), topic: "Ski", subject: "Automatic reply: Ski");
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Equal("N1", Assert.Single(h.Writer.Plans).Id);
        Assert.Equal(Now - TimeSpan.FromMinutes(10), h.Indexes.For("").GetAiDraft("D2")!.Checked);
    }

    [Fact]
    public async Task Replace_falls_back_to_the_source_email()
    {
        using var h = Fx(n1: false);
        h.Seed("N2", at: Now - TimeSpan.FromMinutes(20), topic: "Ski", sender: "noreply@contoso.example");
        h.Seed("N3", at: Now - TimeSpan.FromMinutes(10), topic: "Ski", subject: "Automatic reply: Ski");
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Equal("S1", Assert.Single(h.Writer.Plans).Id);
        var s1 = h.Decision("S1")!;
        Assert.Equal(AiOutcome.Drafted, s1.Outcome);
        Assert.Equal("D2", s1.DraftEntryId);
        Assert.Equal(1, s1.ModelCalls);
    }

    // 6
    [Fact]
    public async Task Prompt_thread_leaves_out_drafts_and_the_old_draft()
    {
        using var h = Fx();
        h.Seed("DR", folder: "Drafts", at: Now - TimeSpan.FromHours(2), topic: "Ski", body: "DRAFTS-ROW");
        h.Seed("D0", folder: "Drafts", at: Now - TimeSpan.FromHours(2.5), topic: "Ski", body: "D0-ROW");
        h.Model.Enqueue(R("keep", 0.9));

        await Pass(h);

        var prompt = Assert.Single(h.Model.Requests).UserPrompt;
        Assert.DoesNotContain("DRAFTS-ROW", prompt);
        Assert.DoesNotContain("D0-ROW", prompt);
        Assert.Contains("BODYTEXT-123", prompt);
        Assert.Single(Regex.Matches(prompt, "N1-BODY"));
    }

    // 7
    [Fact]
    public async Task Old_draft_changed_before_the_move_stays_in_drafts()
    {
        using var h = Fx();
        h.Mailbox.DraftInfo = (id, n) => id == "D0"
            ? FakeAiMailbox.Draft(n < 3 ? FP : new string('b', 64), "Re:: Ski", "OLD-DRAFT-TEXT") : null;
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Single(h.Writer.Plans);
        Assert.Equal(AiDraftState.Left, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Equal("AI reply draft updated (the thread moved on): Re:: Ski. The old draft is still in Drafts because it was changed or is open.",
            Assert.Single(DraftTexts_(h)));
        Assert.DoesNotContain(AiTexts(h), t => t == RevisitTexts.MoveFailed);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: revisit replaced_left");
    }

    // 8
    [Theory]
    [InlineData("changed", AiDraftState.Edited)]
    [InlineData("open", AiDraftState.Open)]
    [InlineData("gone", AiDraftState.Gone)]
    public async Task Old_draft_changed_while_claude_answered_creates_nothing(string kind, AiDraftState expected)
    {
        using var h = Fx();
        h.Mailbox.DraftInfo = (id, n) =>
        {
            if (id != "D0") return null;
            if (n != 2) return FakeAiMailbox.Draft(FP);
            return kind switch
            {
                "changed" => FakeAiMailbox.Draft(new string('b', 64)),
                "open" => FakeAiMailbox.Draft(FP) with { Open = true },
                _ => null,
            };
        };
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Empty(h.Writer.Plans);
        Assert.Empty(h.Mailbox.Moves);
        var d = h.Indexes.For("").GetAiDraft("D0")!;
        Assert.Equal(expected, d.State);
        if (kind == "open") Assert.Equal(Now - H3, d.Checked);
        Assert.Equal([("D0", "replace", 1)], h.RevisitRows());
        Assert.Equal("revisited", h.Decision("N1")!.Rule);
    }

    // 9
    [Fact]
    public async Task Move_that_finds_no_deleted_items_folder_leaves_the_old_draft()
    {
        using var h = Fx();
        h.Mailbox.MoveBehavior = (_, _) => new ObsoleteDraftMove(ObsoleteDraftStatus.NoDeletedItems, "Re:: Ski");
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Equal(AiDraftState.Left, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Equal("AI reply draft updated (the thread moved on): Re:: Ski.", Assert.Single(DraftTexts_(h)));
        Assert.Contains(AiTexts(h), t => t == RevisitTexts.MoveFailed);
    }

    [Fact]
    public async Task Move_that_throws_leaves_the_old_draft()
    {
        using var h = Fx();
        h.Mailbox.MoveThrow = new InvalidOperationException();
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Equal(AiDraftState.Left, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Equal("AI reply draft updated (the thread moved on): Re:: Ski.", Assert.Single(DraftTexts_(h)));
        Assert.Contains(AiTexts(h), t => t == RevisitTexts.MoveFailed);
    }

    [Fact]
    public async Task Outlook_going_away_during_the_move_ends_the_pass()
    {
        using var h = Fx();
        h.Mailbox.MoveThrow = new OutlookUnavailableException();
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.OutlookUnavailable, r.End);
        Assert.Equal(AiDraftState.Left, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Equal(AiOutcome.Drafted, h.Decision("N1")!.Outcome);
    }

    // 10
    [Fact]
    public async Task Create_failure_keeps_the_old_draft_and_the_pass_goes_on()
    {
        using var h = Fx();
        h.Writer.Behavior = (_, n) => n == 1 ? throw new InvalidOperationException() : new RepliedDraft("D3", "Re:: Ski", "", null);
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"), OkReply(0.9));

        var r = await Pass(h);

        Assert.Empty(h.Mailbox.Moves);
        var d = h.Indexes.For("").GetAiDraft("D0")!;
        Assert.Equal(AiDraftState.Open, d.State);
        Assert.Equal(1, d.Attempts);
        Assert.Equal([("D0", "failed", 1)], h.RevisitRows());
        Assert.Contains(AiTexts(h), t => t == DraftTexts.SaveFailed);
        Assert.Equal(2, h.Model.Requests.Count);
        Assert.Equal(DraftModel.ReplySchema, h.Model.Requests[1].JsonSchema);
        Assert.Equal(1, r.Drafted);
        Assert.Equal(1, r.Failed);
    }

    [Fact]
    public async Task Create_failure_item_not_found_has_no_event()
    {
        using var h = Fx();
        h.Writer.Behavior = (_, n) => n == 1 ? throw new ItemNotFoundException("x") : new RepliedDraft("D3", "Re:: Ski", "", null);
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"), OkReply(0.9));

        await Pass(h);

        Assert.Equal([("D0", "failed", 1)], h.RevisitRows());
        Assert.DoesNotContain(AiTexts(h), t => t == DraftTexts.SaveFailed);
        Assert.Empty(h.Mailbox.Moves);
    }

    // 11
    [Fact]
    public async Task Replace_with_meeting_times_reads_headers_and_calendar_before_creating()
    {
        using var h = Fx();
        h.Ai.MeetingTimes = true;
        h.FreeBusy.Free("bob@contoso.example");
        var callsWhenCreated = -1;
        h.Writer.Behavior = (_, _) =>
        {
            callsWhenCreated = h.FreeBusy.Calls.Count;
            return new RepliedDraft("D2", "Re:: Ski", "", FP2);
        };
        h.Model.Enqueue(R("replace", 0.9, "<p>Let's meet.</p>", meeting: "{\"requested\":true,\"duration_minutes\":30}"));

        await Pass(h);

        Assert.NotEmpty(h.FreeBusy.Calls);
        Assert.All(h.FreeBusy.Calls, c => Assert.Equal(WorkPriority.Background, c.Priority));
        Assert.Contains(h.Mailbox.FactCalls, c => c is ("", "N1", WorkPriority.Background));
        Assert.True(callsWhenCreated > 0);
        Assert.Single(h.Writer.Plans);
    }

    // 12
    [Theory]
    [InlineData(ObsoleteDraftStatus.Moved, AiDraftState.Removed)]
    [InlineData(ObsoleteDraftStatus.Open, AiDraftState.Open)]
    [InlineData(ObsoleteDraftStatus.Changed, AiDraftState.Edited)]
    [InlineData(ObsoleteDraftStatus.NotMarked, AiDraftState.Edited)]
    [InlineData(ObsoleteDraftStatus.Gone, AiDraftState.Gone)]
    [InlineData(ObsoleteDraftStatus.NotInDrafts, AiDraftState.Gone)]
    [InlineData(ObsoleteDraftStatus.Sent, AiDraftState.Gone)]
    [InlineData(ObsoleteDraftStatus.NoDeletedItems, AiDraftState.Left)]
    public async Task Remove_follows_the_move_result(ObsoleteDraftStatus status, AiDraftState expected)
    {
        using var h = Fx();
        h.Mailbox.MoveBehavior = (_, _) => new ObsoleteDraftMove(status, "Re:: Ski");
        h.Model.Enqueue(R("remove", 0.9));

        var r = await Pass(h);

        var d = h.Indexes.For("").GetAiDraft("D0")!;
        Assert.Equal(expected, d.State);
        if (status == ObsoleteDraftStatus.Open) Assert.Equal(Now - H3, d.Checked);
        Assert.Equal([("D0", "remove", 1)], h.RevisitRows());
        Assert.Empty(h.Writer.Plans);
        Assert.Equal("revisited", h.Decision("N1")!.Rule);
        if (status == ObsoleteDraftStatus.Moved)
        {
            Assert.Equal("Outdated AI reply draft moved to Deleted Items (no reply needed any more): Re:: Ski",
                Assert.Single(DraftTexts_(h)));
            Assert.Contains("1 moved to Deleted Items", Assert.Single(AiTexts(h)));
            Assert.Equal(1, r.Moved);
            Assert.Equal(N1Time(TimeSpan.FromMinutes(30)), d.Checked);
            foreach (var bad in new[] { "Re:: Ski", "OLD-DRAFT-TEXT", "N1-BODY", "D0", "SECRET-REASON", "bob@" })
                Assert.DoesNotContain(h.Log.Lines, l => l.Contains(bad, StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(0, r.Moved);
            Assert.Empty(DraftTexts_(h));
        }
        if (status == ObsoleteDraftStatus.NoDeletedItems) Assert.Contains(AiTexts(h), t => t == RevisitTexts.MoveFailed);
    }

    [Fact]
    public async Task Remove_with_the_default_fake_moves_the_draft()
    {
        using var h = Fx();
        h.Model.Enqueue(R("remove", 0.9));
        await Pass(h);
        Assert.Equal(["D0"], h.Mailbox.MovedIds);
        Assert.Equal(AiDraftState.Removed, h.Indexes.For("").GetAiDraft("D0")!.State);
    }

    // 13
    [Fact]
    public async Task User_reply_in_sent_items_ends_the_tracking()
    {
        using var h = Fx(n1Ago: Old);
        h.Seed("SENT", folder: "Sent Items", topic: "Ski", at: Now - TimeSpan.FromHours(1), sender: "me@example.org");

        await Pass(h);

        Assert.Equal(AiDraftState.Replied, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Empty(h.Mailbox.DraftReads);
        Assert.Empty(h.Model.Requests);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: revisit replied");
    }

    [Fact]
    public async Task Newer_inbox_row_from_the_user_ends_the_tracking()
    {
        using var h = Fx(n1Ago: Old);
        h.Seed("N0", at: Now - TimeSpan.FromMinutes(100), topic: "Ski", sender: "me@example.org");

        await Pass(h);

        Assert.Equal(AiDraftState.Replied, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Empty(h.Mailbox.DraftReads);
    }

    [Fact]
    public async Task Sent_row_before_the_draft_was_created_is_not_a_reply()
    {
        using var h = Fx(n1Ago: Old);
        h.Seed("SENT", folder: "Sent Items", topic: "Ski", at: Now - H3 + TimeSpan.FromMinutes(1), sender: "me@example.org");
        h.Model.Enqueue(R("keep", 0.9));

        await Pass(h);

        Assert.Single(h.Model.Requests);
        Assert.Equal(AiDraftState.Open, h.Indexes.For("").GetAiDraft("D0")!.State);
    }

    [Fact]
    public async Task Shared_mailbox_draft_is_replied_by_a_sent_row_in_the_own_index()
    {
        using var h = new DraftHarness();
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Seed("SS", mailbox: DraftHarness.Shared, at: Now - H3, topic: "Ski");
        h.Seed("NS", mailbox: DraftHarness.Shared, at: Now - Old, topic: "Ski", sender: "bob@contoso.example");
        h.Track("DS", "SS", Now - H3 + TimeSpan.FromMinutes(5), FP, Now - H3, DraftHarness.Shared);
        h.Seed("SENT", folder: "Sent Items", topic: "Ski", at: Now - TimeSpan.FromHours(1), sender: "me@example.org");

        await Pass(h);

        Assert.Equal(AiDraftState.Replied, h.Indexes.For(DraftHarness.Shared).GetAiDraft("DS")!.State);
        Assert.Empty(h.Mailbox.DraftReads);
    }

    // 14
    [Fact]
    public async Task A_newer_row_in_an_inbox_subfolder_triggers()
    {
        using var h = Fx(n1: false);
        h.Seed("N1", folder: @"Inbox\Trips", at: Now - Old, topic: "Ski", sender: "bob@contoso.example", to: "Ann <ann@contoso.example>");
        h.Model.Enqueue(R("keep", 0.9));
        await Pass(h);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task A_row_not_addressed_to_the_user_can_be_the_target()
    {
        using var h = Fx(n1: false);
        h.Ai.RequireDirectRecipient = true;
        h.Seed("N1", at: Now - Old, topic: "Ski", sender: "bob@contoso.example", to: "list@contoso.example");
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));
        await Pass(h);
        Assert.Equal("N1", Assert.Single(h.Writer.Plans).Id);
    }

    [Fact]
    public async Task A_newer_row_outside_the_inbox_does_not_trigger()
    {
        using var h = Fx(n1: false);
        h.Seed("N1", folder: "Archive", at: Now - Old, topic: "Ski", sender: "bob@contoso.example");
        await Pass(h);
        Assert.Empty(h.Mailbox.DraftReads);
        Assert.Empty(h.Model.Requests);
    }

    // 15
    [Fact]
    public async Task Drafts_older_than_14_days_are_not_revisited()
    {
        using var h = new DraftHarness();
        h.Seed("S1", at: Now.AddDays(-15), topic: "Ski");
        h.Track("D0", "S1", Now.AddDays(-15), FP, Now.AddDays(-15));
        h.Seed("N1", at: Now - Old, topic: "Ski", sender: "bob@contoso.example");
        await Pass(h);
        Assert.Empty(h.Mailbox.DraftReads);
    }

    [Fact]
    public async Task Only_the_newest_open_draft_of_a_thread_is_revisited()
    {
        using var h = Fx(n1Ago: Old);
        h.Track("D9", "S1", Now - TimeSpan.FromHours(2), FP, Now - H3);
        h.Model.Enqueue(R("keep", 0.9));
        await Pass(h);
        Assert.Equal(["D9"], h.Mailbox.DraftReads.Select(x => x.EntryId));
    }

    // 16
    [Fact]
    public async Task At_most_ten_drafts_are_revisited_per_mailbox()
    {
        using var h = new DraftHarness();
        h.Mailbox.DraftInfo = (id, _) => FakeAiMailbox.Draft(FP);
        for (var k = 0; k < 11; k++)
        {
            h.Seed("S" + k, at: Now - TimeSpan.FromHours(4), topic: "T" + k);
            h.Track("D" + k, "S" + k, Now - TimeSpan.FromHours(4), FP, Now - TimeSpan.FromHours(4));
            h.Seed("N" + k, at: Now - TimeSpan.FromHours(2) + TimeSpan.FromMinutes(k), topic: "T" + k, sender: "bob@contoso.example");
            h.Model.Enqueue(R("keep", 0.9));
        }

        await Pass(h);

        Assert.Equal(10, h.Mailbox.DraftReads.Count);
        Assert.DoesNotContain(h.Mailbox.DraftReads, x => x.EntryId == "D0");
        Assert.Equal(10, h.Model.Requests.Count);
    }

    // 17
    [Fact]
    public async Task Open_draft_is_left_alone_until_it_is_closed()
    {
        using var h = Fx(n1Ago: Old);
        h.Mailbox.DraftInfo = (id, n) => id == "D0" ? (n == 1 ? FakeAiMailbox.Draft(FP) with { Open = true } : FakeAiMailbox.Draft(FP)) : null;
        h.Model.Enqueue(R("keep", 0.9));

        await Pass(h);
        Assert.Empty(h.Model.Requests);
        Assert.Equal(AiDraftState.Open, h.Indexes.For("").GetAiDraft("D0")!.State);

        await Pass(h);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task Changed_draft_is_never_read_again()
    {
        using var h = Fx(n1Ago: Old);
        h.Mailbox.DraftInfo = (id, _) => id == "D0" ? FakeAiMailbox.Draft(new string('b', 64)) : null;
        await Pass(h);
        Assert.Equal(AiDraftState.Edited, h.Indexes.For("").GetAiDraft("D0")!.State);
        h.Seed("N5", at: Now - TimeSpan.FromMinutes(80), topic: "Ski", sender: "bob@contoso.example");
        await Pass(h);
        Assert.Single(h.Mailbox.DraftReads);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task Missing_draft_is_gone()
    {
        using var h = Fx(n1Ago: Old);
        h.Mailbox.DraftInfo = (_, _) => null;
        await Pass(h);
        Assert.Equal(AiDraftState.Gone, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Empty(h.Model.Requests);
    }

    [Fact]
    public async Task A_failing_read_changes_nothing_and_the_candidates_still_run()
    {
        using var h = Fx();
        h.Mailbox.DraftReadThrow = new InvalidOperationException();
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Equal(AiDraftState.Open, h.Indexes.For("").GetAiDraft("D0")!.State);
        Assert.Equal(Now - H3, h.Indexes.For("").GetAiDraft("D0")!.Checked);
        Assert.Equal(DraftModel.ReplySchema, Assert.Single(h.Model.Requests).JsonSchema);
        Assert.Equal(1, r.Drafted);
    }

    [Fact]
    public async Task Outlook_going_away_during_the_read_ends_the_pass()
    {
        using var h = Fx();
        h.Mailbox.DraftReadThrow = new OutlookUnavailableException();
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.OutlookUnavailable, r.End);
        Assert.Empty(h.Model.Requests);
    }

    // 18
    [Fact]
    public async Task A_failed_call_counts_an_attempt_and_backs_off()
    {
        using var h = Fx(n1Ago: Old);
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.Failed, r.End);
        Assert.Equal(1, r.Failed);
        var d = h.Indexes.For("").GetAiDraft("D0")!;
        Assert.Equal(1, d.Attempts);
        Assert.Equal(Now - H3, d.Checked);
        Assert.Equal([("D0", "failed", 1)], h.RevisitRows());
        Assert.Contains(AiTexts(h), t => t == DraftTexts.CallFailed("Timeout", 1));
        Assert.Null(h.Decision("N1"));
    }

    [Fact]
    public async Task Invalid_output_is_a_failure()
    {
        using var h = Fx(n1Ago: Old);
        h.Model.Enqueue(Ok("{\"action\":\"delete\",\"confidence\":0.9,\"reason\":\"x\"}"));
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Failed, r.End);
        Assert.Equal([("D0", "failed", 1)], h.RevisitRows());
        Assert.Contains(AiTexts(h), t => t == DraftTexts.CallFailed("BadOutput", 1));
    }

    [Fact]
    public async Task The_third_failed_attempt_gives_the_trigger_up()
    {
        using var h = Fx(n1Ago: Old);
        h.Indexes.For("").SetAiDraftState("D0", AiDraftState.Open, null, 2, Now);
        h.Model.Enqueue(new ModelResult.Failed(ModelFailure.Timeout, null));

        await Pass(h);

        var d = h.Indexes.For("").GetAiDraft("D0")!;
        Assert.Equal(0, d.Attempts);
        Assert.Equal(Now - Old, d.Checked);
    }

    // 19
    [Fact]
    public async Task Rate_limit_writes_no_row_and_backs_off()
    {
        using var h = Fx(n1Ago: Old);
        h.Model.Enqueue(new ModelResult.RateLimited(null));

        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.RateLimited, r.End);
        Assert.Empty(h.RevisitRows());

        var r2 = await Pass(h);
        Assert.Equal(DraftPassEnd.Backoff, r2.End);
        Assert.Single(h.Model.Requests);
    }

    [Fact]
    public async Task Not_signed_in_holds_the_worker()
    {
        using var h = Fx(n1Ago: Old);
        h.Model.Enqueue(new ModelResult.NotSignedIn());

        var r = await Pass(h);

        Assert.Equal(DraftPassEnd.NotSignedIn, r.End);
        Assert.Empty(h.RevisitRows());
        Assert.Equal(DraftTexts.NotSignedInScope, Assert.Single(h.Events(ActivityKind.Error)).Summary);
    }

    // 20
    [Fact]
    public async Task Newer_mail_without_a_decision_gets_a_revisited_row()
    {
        using var h = Fx(n1Ago: TimeSpan.FromMinutes(100));
        h.Seed("N2", at: Now - TimeSpan.FromMinutes(95), topic: "Ski", sender: "bob@contoso.example");
        h.Seed("N3", at: Now - Old, topic: "Ski", sender: "bob@contoso.example");
        var idx = h.Indexes.For("");
        idx.UpsertDecision(new AiDecision("N2", "<N2@contoso.example>", null, Now, AiOutcome.Failed, null, 1, 1));
        idx.UpsertDecision(new AiDecision("N3", "<N3@contoso.example>", null, Now, AiOutcome.NoReply, null, 1, 0, 0.2));
        h.Model.Enqueue(R("keep", 0.9));

        var r = await Pass(h);

        Assert.Equal("revisited", h.Decision("N1")!.Rule);
        var n2 = h.Decision("N2")!;
        Assert.Equal(AiOutcome.Skipped, n2.Outcome);
        Assert.Equal("revisited", n2.Rule);
        Assert.Equal(1, n2.ModelCalls);
        Assert.Equal(1, n2.Attempts);
        Assert.Equal(AiOutcome.NoReply, h.Decision("N3")!.Outcome);
        Assert.Equal(0, r.Skipped);
    }

    // 21
    [Fact]
    public async Task Without_revise_drafts_nothing_is_revisited()
    {
        using var h = Fx();
        h.Ai.ReviseDrafts = false;
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Empty(h.Mailbox.DraftReads);
        Assert.Equal(DraftModel.ReplySchema, Assert.Single(h.Model.Requests).JsonSchema);
        Assert.Equal(1, r.Drafted);
        Assert.Equal(0, r.Updated);
    }

    // 22
    [Fact]
    public async Task Automatic_drafts_are_tracked()
    {
        using var h = new DraftHarness();
        var c = h.Seed("C1");
        h.Writer.Behavior = (_, _) => new RepliedDraft("D1", "Re:: Budget", "", FP2);
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Equal(1, r.Drafted);
        var d = h.Indexes.For("").GetAiDraft("D1")!;
        Assert.Equal("C1", d.SourceEntryId);
        Assert.Equal(c.EventTime, d.Checked);
        Assert.Equal(AiDraftState.Open, d.State);
        Assert.Equal(FP2, d.Fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("XYZ")]
    public async Task Drafts_without_a_valid_fingerprint_are_not_tracked(string? fingerprint)
    {
        using var h = new DraftHarness();
        h.Seed("C1");
        h.Writer.Behavior = (_, _) => new RepliedDraft("D1", "Re:: Budget", "", fingerprint);
        h.Model.Enqueue(OkReply(0.9));

        var r = await Pass(h);

        Assert.Equal(1, r.Drafted);
        Assert.Null(h.Indexes.For("").GetAiDraft("D1"));
    }

    [Fact]
    public async Task Manual_drafts_are_not_tracked()
    {
        using var h = new DraftHarness();
        h.Writer.Behavior = (_, _) => new RepliedDraft("D9", "Re:: Budget", "", FP2);
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail { EntryId = "E1", Folder = "Inbox", EventTime = Now.AddMinutes(-5), Body = "hi" });
        h.Model.Enqueue(OkReply(0.9));

        var r = await h.Worker.DraftSelectedAsync(Ct);

        Assert.True(r.Drafted);
        Assert.Null(h.Indexes.For("").GetAiDraft("D9"));
    }

    // 23
    [Fact]
    public async Task Shared_mailbox_revisits_use_the_shared_mailbox()
    {
        using var h = new DraftHarness();
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.Mailbox.DraftInfo = (id, _) => FakeAiMailbox.Draft(FP);
        h.Seed("SS", mailbox: DraftHarness.Shared, at: Now - H3, topic: "Ski");
        h.Seed("NS", mailbox: DraftHarness.Shared, at: Now - Old, topic: "Ski", sender: "bob@contoso.example");
        h.Track("DS", "SS", Now - H3 + TimeSpan.FromMinutes(5), FP, Now - H3, DraftHarness.Shared);
        h.Writer.Behavior = (_, _) => new RepliedDraft("DN", "Re:: Ski", DraftHarness.Shared, FP2);
        h.Model.Enqueue(R("replace", 0.9, "<p>New</p>"));

        await Pass(h);

        Assert.Equal(DraftHarness.Shared, h.Mailbox.DraftReads[0].Mailbox);
        Assert.Equal(DraftHarness.Shared, Assert.Single(h.Mailbox.Moves).Mailbox);
        Assert.Equal(DraftHarness.Shared, Assert.Single(h.Events(ActivityKind.Draft)).Mailbox);
    }

    // 24
    [Fact]
    public async Task Disabled_drafting_revisits_nothing()
    {
        using var h = Fx();
        h.Ai.Enabled = false;
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.Disabled, r.End);
        Assert.Empty(h.Mailbox.DraftReads);
    }
}
