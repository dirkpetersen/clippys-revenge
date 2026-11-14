using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public class MeetingInvitesTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "Test-7", "Test-7");
    private const string R2 = "Sample, Bob <bob@example.edu>; ext@other.org";

    private const string DisabledMsg =
        "Meeting drafts are disabled in Clippy's Revenge. Ask the user to enable them in the tray settings (Meeting invites: Draft only), then try again.";

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeCalendar : IMeetingCalendar
    {
        public List<MeetingRequest> Resolves { get; } = [];
        public List<(MeetingRequest Req, bool CanBeCanceled)> Creates { get; } = [];
        public ResolvedInvite Resolved { get; set; } = new("Me", "me@example.edu",
            [new InviteAttendee("Sample, Bob", "bob@example.edu", false), new InviteAttendee("ext@other.org", "ext@other.org", true)],
            []);
        public Exception? ResolveThrows { get; set; }
        public Exception? CreateThrows { get; set; }
        public List<string>? Log { get; set; }
        public Action? OnCreate { get; set; }

        public Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct)
        {
            Log?.Add("resolve");
            Resolves.Add(request);
            ct.ThrowIfCancellationRequested();
            if (ResolveThrows is not null) throw ResolveThrows;
            return Task.FromResult(Resolved);
        }

        public Task<CreatedMeeting> CreateAsync(MeetingRequest request, CancellationToken ct)
        {
            Log?.Add("create");
            Creates.Add((request, ct.CanBeCanceled));
            OnCreate?.Invoke();
            if (CreateThrows is not null) throw CreateThrows;
            return Task.FromResult(new CreatedMeeting("E1", "S1"));
        }
    }

    private sealed class Rig
    {
        public FakeCalendar Cal { get; } = new();
        public Settings Settings { get; } = new();
        public ActivityHub Hub { get; } = new();
        public MeetingInvites Svc { get; }

        public List<string> Log { get; } = [];
        public NoteTargets Targets { get; }
        public NoteWriter Writer { get; }

        public Rig(bool withNotes = true)
        {
            Settings.TimeZoneLabel = "PT";
            Cal.Log = Log;
            Targets = new NoteTargets(Log);
            Writer = new NoteWriter(Log);
            var notes = withNotes ? new MeetingNotes(Targets, Writer, () => null) : null;
            Svc = new MeetingInvites(Cal, () => Settings, Hub, Tz, new FixedTime(), notes: notes);
        }

        public Task<MeetingDraftResult> Create(string? recipients = R2, string? subject = "Sync", string? start = "2026-11-17T13:00",
            string? end = "2026-11-17T14:30", string? body = "Agenda", string? location = "Room 1",
            string? mailbox = null, string? replyToId = null, CancellationToken? ct = null) =>
            Svc.CreateDraftAsync(recipients, subject, start, end, body, location, mailbox, replyToId, ct ?? TestContext.Current.CancellationToken);

        public ActivityEvent OneEvent()
        {
            var ev = Hub.GetEvents();
            var e = Assert.Single(ev);
            Assert.Equal(ActivityKind.Invite, e.Kind);
            return e;
        }
    }

    [Fact]
    public async Task Disabled_refuses_first()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = MeetingInviteMode.Disabled;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create());
        Assert.Equal(DisabledMsg, ex.Message);
        Assert.Empty(t.Cal.Resolves);
        Assert.Empty(t.Cal.Creates);
        var e = t.OneEvent();
        Assert.Equal("meeting draft not created: meeting drafts are disabled", e.Summary);
        Assert.Null(e.Mailbox);
    }

    [Fact]
    public async Task Disabled_with_invalid_args()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = MeetingInviteMode.Disabled;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create(recipients: ""));
        Assert.Equal(DisabledMsg, ex.Message);
    }

    [Fact]
    public async Task Undefined_mode_fails_closed()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = (MeetingInviteMode)7;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create());
        Assert.Equal(DisabledMsg, ex.Message);
        Assert.Empty(t.Cal.Resolves);
        Assert.Empty(t.Cal.Creates);
    }

    [Fact]
    public async Task Saves_in_own_calendar()
    {
        var t = new Rig();
        var r = await t.Create();
        Assert.Single(t.Cal.Resolves);
        var (req, cancelable) = Assert.Single(t.Cal.Creates);
        Assert.False(cancelable);
        Assert.Equal("Agenda", req.Body);
        Assert.Equal("Room 1", req.Location);
        Assert.Equal("", req.Mailbox);

        Assert.Equal("saved_as_draft", r.Status);
        Assert.Equal("The meeting was saved in your calendar without sending. It has no Teams or Zoom link: the user adds one in Outlook and sends the invite. It is listed in the Clippy tray under Draft invites.", r.Message);
        Assert.Equal("Sync", r.Subject);
        Assert.Equal("2026-11-17T13:00:00-07:00", r.Start);
        Assert.Equal("2026-11-17T14:30:00-07:00", r.End);
        Assert.Equal("", r.Mailbox);
        Assert.Equal(Recipients.Format([new Recipient("Me", "me@example.edu")]), r.Organizer);
        Assert.Equal(Recipients.Format([new Recipient("Sample, Bob", "bob@example.edu"), new Recipient("ext@other.org", "ext@other.org")]), r.Recipients);
        Assert.Null(r.Category);
        Assert.Null(r.NoteDraftId);
        Assert.Null(r.NoteError);
        Assert.Equal("E1", r.EntryId);
        Assert.Equal("S1", r.StoreId);
    }

    [Fact]
    public async Task Saved_event()
    {
        var t = new Rig();
        await t.Create();
        var e = t.OneEvent();
        Assert.Equal("meeting draft saved for 2 attendees: Tue 11/17 13:00–14:30", e.Summary);
        Assert.Null(e.Mailbox);
    }

    [Fact]
    public async Task Saved_event_singular()
    {
        var t = new Rig();
        t.Cal.Resolved = new ResolvedInvite("Me", "me@example.edu", [new InviteAttendee("Bob", "bob@example.edu", false)], []);
        await t.Create(recipients: "Bob <bob@example.edu>");
        Assert.Equal("meeting draft saved for 1 attendee: Tue 11/17 13:00–14:30", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Shared_mailbox()
    {
        var t = new Rig();
        var r = await t.Create(mailbox: " Help@X.edu ");
        Assert.Equal("help@x.edu", t.Cal.Resolves[0].Mailbox);
        Assert.Equal("help@x.edu", t.Cal.Creates[0].Req.Mailbox);
        Assert.StartsWith("The meeting was saved in the calendar of help@x.edu without sending.", r.Message);
        Assert.Equal("help@x.edu", r.Mailbox);
        Assert.Equal("help@x.edu", t.OneEvent().Mailbox);
    }

    [Fact]
    public async Task Unresolved_creates_nothing()
    {
        var t = new Rig();
        t.Cal.Resolved = t.Cal.Resolved with { Unresolved = ["Nobody <no@x.org>"] };
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create());
        const string msg = "These recipients could not be resolved in Outlook: Nobody <no@x.org>. Nothing was created. Check the addresses and try again.";
        Assert.Equal(msg, ex.Message);
        Assert.Empty(t.Cal.Creates);
        Assert.Equal("meeting draft not created: " + msg, t.OneEvent().Summary);
    }

    [Fact]
    public async Task Bad_args_before_COM()
    {
        var t = new Rig();
        await Assert.ThrowsAsync<McpException>(() => t.Create(start: "2026-11-17"));
        Assert.Empty(t.Cal.Resolves);
        Assert.StartsWith("meeting draft not created: start \"2026-11-17\" is not a valid date-time.", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Past_meeting()
    {
        var t = new Rig();
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create(start: "2026-11-01T10:00", end: "2026-11-01T11:00"));
        Assert.Contains("ends in the past", ex.Message);
        Assert.Empty(t.Cal.Resolves);
    }

    [Fact]
    public async Task Resolve_failure()
    {
        var t = new Rig();
        var thrown = new McpException(InviteTexts.NoCalendarRights("help@x.edu"));
        t.Cal.ResolveThrows = thrown;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create(mailbox: "help@x.edu"));
        Assert.Same(thrown, ex);
        var e = t.OneEvent();
        Assert.StartsWith("meeting draft not created: The user doesn't have delegate", e.Summary);
        Assert.Equal("help@x.edu", e.Mailbox);
    }

    [Fact]
    public async Task Create_failure()
    {
        var t = new Rig();
        var thrown = new InvalidOperationException("boom");
        t.Cal.CreateThrows = thrown;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Create());
        Assert.Same(thrown, ex);
        Assert.Equal("meeting draft not created: boom", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Cancelled_before_resolve()
    {
        var t = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Create(ct: cts.Token));
        Assert.Empty(t.Cal.Creates);
        Assert.Equal("meeting draft not created: the request was cancelled", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Long_reason_truncated()
    {
        var t = new Rig();
        t.Cal.CreateThrows = new InvalidOperationException(new string('x', 500));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Create());
        var s = t.OneEvent().Summary;
        Assert.Equal("meeting draft not created: ".Length + 200, s.Length);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public async Task Body_never_in_events()
    {
        var t = new Rig();
        await t.Create(body: "SECRET-BODY-123", location: "SECRET-ROOM");
        t.Cal.CreateThrows = new InvalidOperationException("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Create(body: "SECRET-BODY-123", location: "SECRET-ROOM"));
        Assert.Equal(2, t.Hub.GetEvents().Count);
        Assert.DoesNotContain(t.Hub.GetEvents(), e => e.Summary.Contains("SECRET-BODY-123") || e.Summary.Contains("SECRET-ROOM"));
    }

    [Fact]
    public async Task Result_json()
    {
        var t = new Rig();
        var r = await t.Create();
        var opts = new[]
        {
            new JsonSerializerOptions(),
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull },
        };
        foreach (var o in opts)
        {
            var j = JsonSerializer.Serialize(r, o);
            Assert.Contains("\"status\":\"saved_as_draft\"", j);
            Assert.Contains("\"category\":null", j);
            foreach (var bad in new[] { "note_draft_id", "note_error", "EntryId", "entry", "StoreId", "teams", "reason", "\"note\"" })
                Assert.DoesNotContain(bad, j);
        }
    }

    private const string NoteLiteral =
        "[[Draft invite saved in your calendar: Tue 11/17 13:00–14:30 PT \"Sync\". Open it from the Clippy tray (Draft invites) or your calendar, add the Teams or Zoom link and send it. Delete this note before you send this reply.]]";

    private const string NoteFailure = "The meeting was saved, but the note in Drafts couldn't be created: Outlook could not create the reply draft: x.";

    [Fact]
    public async Task Note_saved()
    {
        var t = new Rig();
        var r = await t.Create(replyToId: " ID1 ");
        Assert.Equal(["ID1"], t.Targets.Ids);
        Assert.Equal(["check", "resolve", "create", "reply"], t.Log);
        var req = Assert.Single(t.Cal.Creates).Req;
        var (plan, _) = Assert.Single(t.Writer.Calls);
        Assert.Equal("ID1", plan.Id);
        Assert.Equal("tool", plan.Marker);
        Assert.False(plan.ReplyAll);
        Assert.Null(plan.Mailbox);
        Assert.Equal(MeetingNoteText.Html(req.Start, req.End, "PT", "Sync", ""), plan.HtmlFragment);
        Assert.Equal("D1", r.NoteDraftId);
        Assert.Null(r.NoteError);
        Assert.Equal("The meeting was saved in your calendar without sending. It has no Teams or Zoom link: the user adds one in Outlook and sends the invite. It is listed in the Clippy tray under Draft invites. A note was saved in Drafts as a reply to the email.", r.Message);
        Assert.Equal("meeting draft saved for 2 attendees: Tue 11/17 13:00–14:30", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Note_exact_text_in_plan()
    {
        var t = new Rig();
        await t.Create(replyToId: "ID1");
        var plan = Assert.Single(t.Writer.Calls).Plan;
        Assert.Contains("<span style=\"background-color:#ffff00\">" + NoteLiteral + "</span>", plan.HtmlFragment);
    }

    [Fact]
    public async Task Note_label_default()
    {
        var t = new Rig();
        t.Settings.TimeZoneLabel = "";
        await t.Create(replyToId: "ID1");
        var req = Assert.Single(t.Cal.Creates).Req;
        var plan = Assert.Single(t.Writer.Calls).Plan;
        Assert.Contains("13:00–14:30 " + TimeZoneLabels.For(Tz, req.Start) + " \"Sync\"", plan.HtmlFragment);
    }

    [Fact]
    public async Task Unknown_reply_id_creates_nothing()
    {
        var t = new Rig();
        var thrown = new ItemNotFoundException("ID9");
        t.Targets.Throw = thrown;
        var ex = await Assert.ThrowsAsync<ItemNotFoundException>(() => t.Create(replyToId: "ID9"));
        Assert.Same(thrown, ex);
        Assert.Empty(t.Cal.Resolves);
        Assert.Empty(t.Cal.Creates);
        Assert.Empty(t.Writer.Calls);
        Assert.Equal("meeting draft not created: " + TrayText.Truncate(ex.Message, 200), t.OneEvent().Summary);
    }

    [Fact]
    public async Task Reply_id_without_index()
    {
        var t = new Rig(withNotes: false);
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create(replyToId: "ID1"));
        Assert.Equal("The email index could not be opened; see the tray (Error state). Restart Clippy or use Rebuild index.", ex.Message);
        Assert.Empty(t.Cal.Resolves);
        Assert.Empty(t.Cal.Creates);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public async Task Blank_reply_id_is_no_note(string id, bool withNotes)
    {
        var t = new Rig(withNotes);
        var r = await t.Create(replyToId: id);
        Assert.Empty(t.Targets.Ids);
        Assert.Empty(t.Writer.Calls);
        Assert.Null(r.NoteDraftId);
        Assert.Null(r.NoteError);
        Assert.DoesNotContain("A note was saved", r.Message);
    }

    [Fact]
    public async Task Disabled_refuses_before_check()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = MeetingInviteMode.Disabled;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Create(replyToId: "ID1"));
        Assert.Equal(DisabledMsg, ex.Message);
        Assert.Empty(t.Targets.Ids);
    }

    [Fact]
    public async Task Bad_args_before_check()
    {
        var t = new Rig();
        await Assert.ThrowsAsync<McpException>(() => t.Create(start: "2026-11-17", replyToId: "ID1"));
        Assert.Empty(t.Targets.Ids);
    }

    [Fact]
    public async Task Check_before_recipients()
    {
        var t = new Rig();
        t.Cal.Resolved = t.Cal.Resolved with { Unresolved = ["Nobody <no@x.org>"] };
        t.Targets.Throw = new ItemNotFoundException("ID9");
        await Assert.ThrowsAsync<ItemNotFoundException>(() => t.Create(replyToId: "ID9"));
        Assert.Empty(t.Cal.Resolves);
    }

    [Fact]
    public async Task Cancelled_during_check()
    {
        var t = new Rig();
        t.Targets.Throw = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Create(replyToId: "ID1"));
        Assert.Equal("meeting draft not created: the request was cancelled", t.OneEvent().Summary);
        Assert.Empty(t.Cal.Creates);
    }

    [Fact]
    public async Task Note_failure_keeps_meeting()
    {
        var t = new Rig();
        t.Writer.Throw = new McpException("Outlook could not create the reply draft: x");
        var r = await t.Create(replyToId: "ID1");
        Assert.Equal("saved_as_draft", r.Status);
        Assert.Equal("E1", r.EntryId);
        Assert.Single(t.Cal.Creates);
        Assert.Null(r.NoteDraftId);
        Assert.Equal(NoteFailure, r.NoteError);
        Assert.DoesNotContain("A note was saved", r.Message);
        Assert.StartsWith("meeting draft saved for", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Note_runs_after_cancel()
    {
        var t = new Rig();
        using var cts = new CancellationTokenSource();
        t.Cal.OnCreate = () => cts.Cancel();
        var r = await t.Create(replyToId: "ID1", ct: cts.Token);
        var (_, cancelable) = Assert.Single(t.Writer.Calls);
        Assert.False(cancelable);
        Assert.Equal("D1", r.NoteDraftId);
    }

    [Fact]
    public async Task Note_for_shared_meeting()
    {
        var t = new Rig();
        await t.Create(mailbox: "help@x.edu", replyToId: "ID1");
        var plan = Assert.Single(t.Writer.Calls).Plan;
        Assert.Null(plan.Mailbox);
        Assert.Contains("saved in the calendar of help@x.edu:", plan.HtmlFragment);
        Assert.Contains("or that calendar,", plan.HtmlFragment);
    }

    [Fact]
    public async Task Result_json_with_note()
    {
        var ok = JsonSerializer.Serialize(await new Rig().Create(replyToId: "ID1"));
        Assert.Contains("\"note_draft_id\":\"D1\"", ok);
        Assert.DoesNotContain("note_error", ok);

        var bad = new Rig();
        bad.Writer.Throw = new McpException("Outlook could not create the reply draft: x");
        var failed = JsonSerializer.Serialize(await bad.Create(replyToId: "ID1"));
        Assert.Contains("\"note_error\":\"The meeting was saved, but", failed);
        Assert.DoesNotContain("note_draft_id", failed);

        var none = JsonSerializer.Serialize(await new Rig().Create());
        Assert.DoesNotContain("note_draft_id", none);
        Assert.DoesNotContain("note_error", none);
    }

    [Fact]
    public void Tool_description_reply_to_id()
    {
        var d = InviteTexts.ToolDescription;
        foreach (var s in new[] { "reply_to_id", "search_emails", "note", "unknown id means nothing is created", "Nothing is sent", "find_free_times", "next occurrence" })
            Assert.Contains(s, d);
    }

    [Fact]
    public void When_format()
    {
        Assert.Equal("Tue 11/17 13:00–14:30", InviteTexts.When(new DateTime(2026, 11, 17, 13, 0, 0), new DateTime(2026, 11, 17, 14, 30, 0)));
        Assert.Equal("Tue 11/17 23:00–00:00", InviteTexts.When(new DateTime(2026, 11, 17, 23, 0, 0), new DateTime(2026, 11, 18, 0, 0, 0)));
    }

    [Fact]
    public void Calendar_text()
    {
        Assert.Equal("your calendar", InviteTexts.Calendar(""));
        Assert.Equal("the calendar of a@x.org", InviteTexts.Calendar("a@x.org"));
    }

    [Fact]
    public void Tool_description()
    {
        var d = InviteTexts.ToolDescription;
        foreach (var s in new[] { "Nothing is sent", "Teams or Zoom link", "find_free_times", "next occurrence", "nothing is created" })
            Assert.Contains(s, d);
        Assert.DoesNotContain("send_meeting_invite", d);
        Assert.DoesNotContain("confirm", d, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Send mode", d);
    }

    [Fact]
    public void Invite_texts()
    {
        Assert.Contains("a@x.org; B <b@x.org>", InviteTexts.Unresolved(["a@x.org", "B <b@x.org>"]));
        Assert.Equal("The user doesn't have delegate or editor rights on the calendar of s@x.org, so the meeting can't be organized from it. Ask the user to get editor or delegate access from the mailbox owner, or leave mailbox empty to organize it from their own calendar.",
            InviteTexts.NoCalendarRights("s@x.org"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Internal_kinds(int k) => Assert.True(AttendeeKinds.IsInternal(k));

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(-1)]
    [InlineData(99)]
    public void External_kinds(int k) => Assert.False(AttendeeKinds.IsInternal(k));
}
