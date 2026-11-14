using System.Globalization;
using System.Text.Json;
using static ClippysRevenge.Core.Tests.FakeDraftModel;

namespace ClippysRevenge.Core.Tests;

public class DraftWorkerMeetingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string H(string x) => DraftHtml.HighlightOpen + x + DraftHtml.HighlightClose;

    private const string MBlock =
        "1. Mon 11/16&nbsp; 09:00–10:00 PT<br>2. Mon 11/16&nbsp; 11:00–12:00 PT<br>3. Mon 11/16&nbsp; 08:00–09:00 PT";

    private const string EuropeBlock =
        "1. Mon 11/16&nbsp; 08:00–09:00 PT&nbsp; (17:00–18:00 CET)<br>2. Tue 11/17&nbsp; 08:00–09:00 PT&nbsp; (17:00–18:00 CET)"
        + "<br><br>Outside European office hours:<br>3. Mon 11/16&nbsp; 09:00–10:00 PT&nbsp; (18:00–19:00 CET)";

    private const string EuropeHeaders = "Date: Fri, 13 Nov 2026 21:00:00 +0100\r\nSubject: x\r\n";

    private static ModelResult MeetingReply(string? meetingJson = """{"requested":true}""",
        string body = "<p>Happy to meet. These times work for me:</p><p>[[meeting_times]]</p><p>Best, Dirk</p>",
        bool replyAll = false, double confidence = 0.9)
    {
        var meeting = meetingJson is null ? "" : ",\"meeting\":" + meetingJson;
        var json = "{\"needs_reply\":true,\"confidence\":" + confidence.ToString("0.###", CultureInfo.InvariantCulture)
            + ",\"reply_all\":" + (replyAll ? "true" : "false")
            + ",\"body_html\":" + JsonSerializer.Serialize(body) + meeting + ",\"reason\":\"SECRET-REASON\"}";
        return Ok(json);
    }

    private static DraftHarness Make(int options = 3)
    {
        var h = new DraftHarness();
        h.Settings.TimeZoneLabel = "PT";
        h.Ai.MeetingTimeOptions = options;
        return h;
    }

    private static void ScenarioM(DraftHarness h, string sender = "ann@contoso.example")
    {
        h.FreeBusy.Calendars["me@example.org"] = [(new DateTime(2026, 11, 16, 10, 0, 0), new DateTime(2026, 11, 16, 11, 0, 0), '2')];
        h.FreeBusy.Free(sender);
    }

    private static Task<DraftPassResult> Pass(DraftHarness h, CancellationToken? ct = null) => h.Worker.RunPassAsync(ct ?? Ct);

    // ---------------------------------------------------------------- automatic

    [Fact]
    public async Task Offer_inserted_automatic()
    {
        using var h = Make();
        ScenarioM(h);
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());

        var r = await Pass(h);

        Assert.Equal(1, r.Drafted);
        var plan = Assert.Single(h.Writer.Plans);
        Assert.Contains(MBlock, plan.HtmlFragment);
        Assert.DoesNotContain("meeting_times", plan.HtmlFragment, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(h.FreeBusy.Calls);
        Assert.All(h.FreeBusy.Calls, c => Assert.Equal(WorkPriority.Background, c.Priority));
        Assert.Equal(AiOutcome.Drafted, h.Decision("C1")!.Outcome);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: offered 3 meeting times (2 of 2 attendees readable)");
        foreach (var bad in new[] { "contoso", "11/16", "09:00" })
            Assert.DoesNotContain(h.AllText(), t => t.Contains(bad, StringComparison.Ordinal));
    }

    [Fact]
    public async Task System_prompt_has_meeting_note_when_on()
    {
        using var h = Make();
        h.Seed("C1");
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var on = Assert.Single(h.Model.Requests).SystemPrompt;
        Assert.Contains("Meeting times:\n", on);
        Assert.Contains("[[meeting_times]]", on);

        h.Ai.MeetingTimes = false;
        h.Seed("C2", age: TimeSpan.FromMinutes(4));
        h.Model.Enqueue(OkReply(0.9));
        await Pass(h);
        var off = h.Model.Requests[1].SystemPrompt;
        Assert.DoesNotContain("Meeting times:\n", off);
        Assert.DoesNotContain("[[meeting_times]]", off);
    }

    [Fact]
    public async Task Free_busy_only_after_the_model()
    {
        using var h = Make();
        ScenarioM(h);
        h.Seed("C1");
        h.Model.OnRun = (_, _) =>
        {
            Assert.Empty(h.FreeBusy.Calls);
            return Task.FromResult(MeetingReply());
        };
        await Pass(h);
        Assert.NotEmpty(h.FreeBusy.Calls);
    }

    [Fact]
    public async Task Europe_from_facts_headers()
    {
        using var h = Make();
        ScenarioM(h);
        h.Seed("C1");
        h.Mailbox.Facts = (_, _) => FakeAiMailbox.DefaultFacts with { TransportHeaders = EuropeHeaders };
        h.Model.Enqueue(MeetingReply("""{"requested":true,"latest_date":"2026-11-17"}"""));
        await Pass(h);
        Assert.Contains(EuropeBlock, Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Equal(2, h.Mailbox.FactCalls.Count);
    }

    [Fact]
    public async Task Not_europe_without_headers_generic_tld()
    {
        using var h = Make();
        ScenarioM(h);
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());
        await Pass(h);
        var html = Assert.Single(h.Writer.Plans).HtmlFragment;
        Assert.Contains(MBlock, html);
        Assert.DoesNotContain("CET", html);
    }

    [Fact]
    public async Task Owner_unknown_gives_calendar_placeholder()
    {
        using var h = Make();
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());
        var r = await Pass(h);
        Assert.Equal(1, r.Drafted);
        Assert.Contains(H("[[add meeting times: Clippy couldn't read your calendar]]"), Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: couldn't read the calendar for meeting times (unknown)");
    }

    [Fact]
    public async Task No_slots_gives_range_placeholder()
    {
        using var h = Make();
        h.FreeBusy.Calendars["me@example.org"] = [(new DateTime(2026, 11, 14), new DateTime(2026, 11, 24), '2')];
        h.FreeBusy.Free("ann@contoso.example");
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());
        var r = await Pass(h);
        Assert.Equal(1, r.Drafted);
        Assert.Contains(H("[[add meeting times: no free time found between 2026-11-14 and 2026-11-23]]"),
            Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Contains(h.Log.Lines, l => l == "AI drafts: no free meeting times to offer (2 of 2 attendees readable)");
    }

    [Fact]
    public async Task Outlook_gone_during_free_busy()
    {
        using var h = Make();
        ScenarioM(h);
        h.FreeBusy.Throws["me@example.org"] = new OutlookUnavailableException();
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());
        var r = await Pass(h);
        Assert.Equal(DraftPassEnd.OutlookUnavailable, r.End);
        Assert.Equal(1, r.ModelCalls);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Failed, 1, 1), (d.Outcome, d.ModelCalls, d.Attempts));
        Assert.Empty(h.Writer.Plans);
        Assert.Single(h.Mailbox.FactCalls);
        Assert.DoesNotContain(h.Events(ActivityKind.Ai), e => e.Summary.Contains("call failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Meeting_times_off()
    {
        using var h = Make();
        ScenarioM(h);
        h.Ai.MeetingTimes = false;
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());
        await Pass(h);
        Assert.Contains(H("[[meeting_times]]"), Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.Empty(h.FreeBusy.Calls);
    }

    [Fact]
    public async Task Placeholder_without_meeting_object()
    {
        using var h = Make();
        ScenarioM(h);
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply(meetingJson: null));
        await Pass(h);
        Assert.Contains(MBlock, Assert.Single(h.Writer.Plans).HtmlFragment);
    }

    [Fact]
    public async Task Reply_all_attendees()
    {
        using var h = Make();
        h.FreeBusy.Free("me@example.org", "ann@contoso.example", "bob@x.org", "carol@y.org");
        h.Seed("C1", to: "Me <me@example.org>; Bob <bob@x.org>", cc: "carol@y.org");
        h.Model.Enqueue(MeetingReply(replyAll: true));
        await Pass(h);
        Assert.Equal(["me@example.org", "ann@contoso.example", "bob@x.org", "carol@y.org"], h.FreeBusy.Calls.Select(c => c.Email));
        Assert.True(Assert.Single(h.Writer.Plans).ReplyAll);
    }

    [Fact]
    public async Task Not_reply_all_only_checks_the_sender()
    {
        using var h = Make();
        h.FreeBusy.Free("me@example.org", "ann@contoso.example", "bob@x.org", "carol@y.org");
        h.Seed("C1", to: "Me <me@example.org>; Bob <bob@x.org>", cc: "carol@y.org");
        h.Model.Enqueue(MeetingReply(replyAll: false));
        await Pass(h);
        Assert.Equal(["me@example.org", "ann@contoso.example"], h.FreeBusy.Calls.Select(c => c.Email));
    }

    [Fact]
    public async Task Shared_mailbox_owner_is_me()
    {
        using var h = Make();
        h.Settings.SharedMailboxes[0].AiDrafts = true;
        h.FreeBusy.Free("me@example.org", "ann@contoso.example", "bob@x.org");
        h.Seed("C1", mailbox: DraftHarness.Shared, to: "Helpdesk <helpdesk@example.org>; Bob <bob@x.org>");
        h.Model.Enqueue(MeetingReply(replyAll: true));
        var r = await Pass(h);
        Assert.Equal(1, r.Drafted);
        Assert.Equal(["me@example.org", "ann@contoso.example", "bob@x.org"], h.FreeBusy.Calls.Select(c => c.Email));
    }

    [Fact]
    public async Task Cet_missing_warns_once()
    {
        using var h = Make();
        ScenarioM(h);
        h.Cet = () => null;
        h.Mailbox.Facts = (_, _) => FakeAiMailbox.DefaultFacts with { TransportHeaders = EuropeHeaders };
        h.Seed("C1", age: TimeSpan.FromMinutes(3), topic: "T1");
        h.Seed("C2", age: TimeSpan.FromMinutes(4), topic: "T2");
        h.Model.Enqueue(MeetingReply(), MeetingReply());
        var r = await Pass(h);
        Assert.Equal(2, r.Drafted);
        Assert.Single(h.Log.Lines, l => l == FreeTimeFinder.CetMissingWarning);
        Assert.All(h.Writer.Plans, p => Assert.DoesNotContain("CET", p.HtmlFragment));
    }

    [Fact]
    public async Task Cancelled_during_free_busy()
    {
        using var h = Make();
        ScenarioM(h);
        using var cts = new CancellationTokenSource();
        h.FreeBusy.OnCall = _ => cts.Cancel();
        h.FreeBusy.Throws["me@example.org"] = new OperationCanceledException();
        h.Seed("C1");
        h.Model.Enqueue(MeetingReply());
        var r = await Pass(h, cts.Token);
        Assert.Equal(DraftPassEnd.Cancelled, r.End);
        var d = h.Decision("C1")!;
        Assert.Equal((AiOutcome.Failed, 1), (d.Outcome, d.ModelCalls));
        Assert.Empty(h.Writer.Plans);
    }

    // ---------------------------------------------------------------- manual

    private static DraftHarness MakeManual(string sender = "ann@contoso.example", int options = 3)
    {
        var h = Make(options);
        h.Ai.Enabled = false;
        h.Mailbox.Selected = new SelectedEmail("E1", "S1");
        h.Mailbox.Live = new LiveEmail("", new IndexedEmail
        {
            EntryId = "E1",
            Folder = "Inbox",
            Subject = "Budget",
            SenderEmail = sender,
            To = "Me <me@example.org>",
            ConversationTopic = "Budget",
            Body = "BODYTEXT-123",
            EventTime = DraftHarness.Now.AddMinutes(-5),
        });
        return h;
    }

    private static string NotRunning => DraftTexts.ManualFailed("Outlook isn't running");

    [Fact]
    public async Task Offer_inserted_manual()
    {
        using var h = MakeManual();
        ScenarioM(h);
        h.Model.Enqueue(MeetingReply());
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(new ManualDraftResult(true, DraftTexts.ManualDone), r);
        Assert.Contains(MBlock, Assert.Single(h.Writer.Plans).HtmlFragment);
        Assert.All(h.FreeBusy.Calls, c => Assert.Equal(WorkPriority.Interactive, c.Priority));
        Assert.NotEmpty(h.FreeBusy.Calls);
        Assert.Equal([("", "E1", WorkPriority.Interactive)], h.Mailbox.FactCalls);
    }

    [Fact]
    public async Task Manual_without_meeting_reads_no_facts()
    {
        using var h = MakeManual();
        ScenarioM(h);
        h.Model.Enqueue(OkReply(0.9, body: "<p>Sure</p>"));
        await h.Worker.DraftSelectedAsync(Ct);
        Assert.Empty(h.Mailbox.FactCalls);
        Assert.Empty(h.FreeBusy.Calls);
    }

    [Fact]
    public async Task Manual_facts_fail_uses_tld()
    {
        using var h = MakeManual("anna@firma.de");
        ScenarioM(h, "anna@firma.de");
        h.Mailbox.FactsThrow = new InvalidOperationException();
        h.Model.Enqueue(MeetingReply());
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.True(r.Drafted);
        Assert.Contains("(17:00–18:00 CET)", Assert.Single(h.Writer.Plans).HtmlFragment);

        using var g = MakeManual("ann@contoso.example");
        ScenarioM(g);
        g.Mailbox.FactsThrow = new InvalidOperationException();
        g.Model.Enqueue(MeetingReply());
        await g.Worker.DraftSelectedAsync(Ct);
        Assert.DoesNotContain("CET", Assert.Single(g.Writer.Plans).HtmlFragment);
    }

    [Fact]
    public async Task Manual_facts_outlook_gone()
    {
        using var h = MakeManual();
        ScenarioM(h);
        h.Mailbox.FactsThrow = new OutlookUnavailableException();
        h.Model.Enqueue(MeetingReply());
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(new ManualDraftResult(false, "I couldn't draft that reply: Outlook isn't running"), r);
        Assert.Empty(h.Writer.Plans);
        var d = h.Decision("E1")!;
        Assert.Equal((AiOutcome.Failed, 1), (d.Outcome, d.ModelCalls));
    }

    [Fact]
    public async Task Manual_free_busy_outlook_gone()
    {
        using var h = MakeManual();
        ScenarioM(h);
        h.FreeBusy.Throws["me@example.org"] = new OutlookUnavailableException();
        h.Model.Enqueue(MeetingReply());
        var r = await h.Worker.DraftSelectedAsync(Ct);
        Assert.Equal(new ManualDraftResult(false, NotRunning), r);
        Assert.Empty(h.Writer.Plans);
        var d = h.Decision("E1")!;
        Assert.Equal((AiOutcome.Failed, 1), (d.Outcome, d.ModelCalls));
    }

    [Fact]
    public async Task Manual_meeting_note_in_prompt()
    {
        using var h = MakeManual();
        ScenarioM(h);
        h.Model.Enqueue(MeetingReply());
        await h.Worker.DraftSelectedAsync(Ct);
        var prompt = Assert.Single(h.Model.Requests).SystemPrompt;
        Assert.Contains("Meeting times:\n", prompt);
        Assert.Contains("asked for a reply to this email", prompt);
    }
}
