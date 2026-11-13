using System.Text.Json;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public class MeetingInvitesTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "Test-7", "Test-7");
    private const string R2 = "Petersen, Dirk <dirk@osu.edu>; ext@other.org";

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 16, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeCalendar : IMeetingCalendar
    {
        public List<MeetingRequest> Resolves { get; } = [];
        public List<(MeetingRequest Req, InviteDecision Decision, bool Cancelable)> Creates { get; } = [];
        public ResolvedInvite Resolved { get; set; } = new("Me", "me@osu.edu",
            [new InviteAttendee("Petersen, Dirk", "dirk@osu.edu", false), new InviteAttendee("ext@other.org", "ext@other.org", true)],
            [], false);
        public bool TeamsAdded { get; set; }
        public Exception? ResolveThrows { get; set; }
        public Exception? CreateThrows { get; set; }

        public Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct)
        {
            Resolves.Add(request);
            if (ResolveThrows is not null) throw ResolveThrows;
            return Task.FromResult(Resolved);
        }

        public Task<CreatedMeeting> CreateAsync(MeetingRequest request, InviteDecision decision, CancellationToken ct)
        {
            Creates.Add((request, decision, ct.CanBeCanceled));
            if (CreateThrows is not null) throw CreateThrows;
            return Task.FromResult(new CreatedMeeting(TeamsAdded));
        }
    }

    private sealed class FakeConfirmer : IInviteConfirmer
    {
        public List<(InvitePreview Preview, TimeSpan Timeout)> Calls { get; } = [];
        public InviteConfirmation Answer { get; set; } = new(InviteChoice.Send, false);
        public Exception? Throws { get; set; }
        public Action? Hook { get; set; }

        public Task<InviteConfirmation> ConfirmAsync(InvitePreview preview, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((preview, timeout));
            Hook?.Invoke();
            if (Throws is not null) throw Throws;
            return Task.FromResult(Answer);
        }
    }

    private sealed class Rig
    {
        public FakeCalendar Cal { get; } = new();
        public FakeConfirmer Conf { get; } = new();
        public Settings Settings { get; } = new();
        public ActivityHub Hub { get; } = new();
        public BalloonCenter Balloons { get; } = new();
        public List<Balloon> Shown { get; } = [];
        public MeetingInvites Svc { get; }

        public Rig()
        {
            Balloons.Shown += Shown.Add;
            Svc = new MeetingInvites(Cal, Conf, () => Settings, Hub, Balloons, Tz, new FixedTime());
        }

        public Task<MeetingInviteResult> Send(string? recipients = R2, string? subject = "Sync", string? start = "2026-11-17T13:00",
            string? end = "2026-11-17T14:30", string? body = "Agenda", string? location = "Room 1", bool teams = false,
            string? mailbox = null, CancellationToken? ct = null) =>
            Svc.SendAsync(recipients, subject, start, end, body, location, teams, mailbox, ct ?? TestContext.Current.CancellationToken);

        public ActivityEvent OneEvent()
        {
            var ev = Hub.GetEvents();
            var e = Assert.Single(ev);
            Assert.Equal(ActivityKind.Invite, e.Kind);
            return e;
        }
    }

    private const string DraftOnlyMsg = "Meeting invites are set to Draft only, so the meeting is saved unsent in your calendar. The user needs to review it in Outlook and send it from there.";

    [Fact]
    public async Task Disabled_refuses_first()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = MeetingInviteMode.Disabled;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Send());
        Assert.Equal(InviteGate.DisabledMessage, ex.Message);
        Assert.Empty(t.Cal.Resolves);
        Assert.Empty(t.Conf.Calls);
        Assert.Empty(t.Cal.Creates);
        var e = t.OneEvent();
        Assert.Equal("Invite refused: invites are disabled", e.Summary);
        Assert.Null(e.Mailbox);
    }

    [Fact]
    public async Task Disabled_with_invalid_args()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = MeetingInviteMode.Disabled;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Send(recipients: ""));
        Assert.Equal(InviteGate.DisabledMessage, ex.Message);
    }

    [Fact]
    public async Task DraftOnly_saves_without_dialog()
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = MeetingInviteMode.DraftOnly;
        var r = await t.Send();
        Assert.Single(t.Cal.Resolves);
        Assert.Empty(t.Conf.Calls);
        Assert.Equal(InviteDecision.SaveDraft(InviteDraftReason.DraftOnlyMode), Assert.Single(t.Cal.Creates).Decision);
        Assert.Equal("saved_as_draft", r.Status);
        Assert.Equal("draft_only_mode", r.Reason);
        Assert.Equal(DraftOnlyMsg, r.Message);
        Assert.Equal("Invite saved as draft (draft_only_mode): \"Sync\"", t.OneEvent().Summary);
        Assert.Empty(t.Shown);
    }

    [Fact]
    public async Task Send_click()
    {
        var t = new Rig();
        var r = await t.Send();
        Assert.Equal(InviteDecision.Send, Assert.Single(t.Cal.Creates).Decision);
        Assert.Equal("sent", r.Status);
        Assert.Null(r.Reason);
        Assert.Equal("The invite was sent to 2 recipients from your calendar.", r.Message);
        Assert.Equal("2026-11-17T13:00:00-07:00", r.Start);
        Assert.Equal("2026-11-17T14:30:00-07:00", r.End);
        Assert.Equal("", r.Mailbox);
        Assert.Equal("Me <me@osu.edu>", r.Organizer);
        Assert.Equal("Petersen, Dirk <dirk@osu.edu>; ext@other.org <ext@other.org>", r.Recipients);
        Assert.False(r.Teams);
        Assert.Null(r.Note);
        Assert.Equal("It looks like you're trying to schedule a meeting. I sent 2 invites.", Assert.Single(t.Shown).Text);
        Assert.Equal("Invite sent: \"Sync\" to 2 recipients", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Preview_contents_and_timeout()
    {
        var t = new Rig();
        await t.Send();
        var (p, timeout) = Assert.Single(t.Conf.Calls);
        Assert.Equal("Me", p.OrganizerName);
        Assert.Equal("me@osu.edu", p.OrganizerEmail);
        Assert.Equal("", p.Mailbox);
        Assert.Equal("Sync", p.Subject);
        Assert.Equal(new DateTime(2026, 11, 17, 13, 0, 0), p.Start);
        Assert.Equal(new DateTime(2026, 11, 17, 14, 30, 0), p.End);
        Assert.Equal("Room 1", p.Location);
        Assert.False(p.TeamsRequested);
        Assert.Equal(t.Cal.Resolved.Attendees, p.Attendees);
        Assert.Equal("Agenda", p.Body);
        Assert.Equal(TimeSpan.FromSeconds(120), timeout);

        var t2 = new Rig();
        t2.Settings.InviteConfirmTimeoutSeconds = 45;
        await t2.Send();
        Assert.Equal(TimeSpan.FromSeconds(45), t2.Conf.Calls[0].Timeout);
    }

    [Fact]
    public async Task One_attendee()
    {
        var t = new Rig();
        t.Cal.Resolved = t.Cal.Resolved with { Attendees = [new InviteAttendee("A", "a@x.org", false)] };
        var r = await t.Send(recipients: "a@x.org");
        Assert.Equal("The invite was sent to 1 recipient from your calendar.", r.Message);
        Assert.Equal("It looks like you're trying to schedule a meeting. I sent 1 invite.", Assert.Single(t.Shown).Text);
        Assert.Equal("Invite sent: \"Sync\" to 1 recipient", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Save_as_draft_click()
    {
        var t = new Rig();
        t.Conf.Answer = new(InviteChoice.SaveDraft, false);
        var r = await t.Send();
        Assert.Equal(InviteDecision.SaveDraft(InviteDraftReason.UserChoice), Assert.Single(t.Cal.Creates).Decision);
        Assert.Equal("user_choice", r.Reason);
        Assert.Equal("The user chose Save as draft. The meeting is saved unsent in your calendar; the user needs to review it in Outlook and send it from there.", r.Message);
        Assert.Empty(t.Shown);
        t.OneEvent();
    }

    [Fact]
    public async Task Timeout()
    {
        var t = new Rig();
        t.Conf.Answer = new(null, true);
        var r = await t.Send();
        Assert.Equal(InviteDecision.SaveDraft(InviteDraftReason.Timeout), Assert.Single(t.Cal.Creates).Decision);
        Assert.Equal("timeout", r.Reason);
        Assert.Equal("Nobody answered the confirmation dialog within 120 seconds, so the meeting is saved unsent in your calendar. The user needs to review it in Outlook and send it from there.", r.Message);
        Assert.Equal("It looks like you stepped away. I saved \"Sync\" as a draft — review it in Outlook.", Assert.Single(t.Shown).Text);
        t.OneEvent();
    }

    [Fact]
    public async Task Send_racing_timeout_saves_draft()
    {
        var t = new Rig();
        t.Conf.Answer = new(InviteChoice.Send, true);
        await t.Send();
        Assert.Equal(InviteDecision.SaveDraft(InviteDraftReason.Timeout), Assert.Single(t.Cal.Creates).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel(bool timedOut)
    {
        var t = new Rig();
        t.Conf.Answer = new(InviteChoice.Cancel, timedOut);
        var r = await t.Send();
        Assert.Empty(t.Cal.Creates);
        Assert.Equal("cancelled", r.Status);
        Assert.Equal(InviteTexts.Cancelled, r.Message);
        Assert.Equal("{\"status\":\"cancelled\",\"message\":\"The user declined this invite. Do not retry unless they ask.\"}",
            JsonSerializer.Serialize(r));
        Assert.Equal("Invite cancelled by the user: \"Sync\"", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Dialog_closed_without_choice_cancels()
    {
        var t = new Rig();
        t.Conf.Answer = new(null, false);
        var r = await t.Send();
        Assert.Equal("cancelled", r.Status);
        Assert.Empty(t.Cal.Creates);
    }

    [Fact]
    public async Task Mode_downgraded_to_disabled_during_dialog()
    {
        var t = new Rig();
        t.Conf.Hook = () => t.Settings.MeetingInviteMode = MeetingInviteMode.Disabled;
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Send());
        Assert.Equal(InviteGate.DisabledMessage, ex.Message);
        Assert.Empty(t.Cal.Creates);
        Assert.StartsWith("Invite refused", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Mode_downgraded_to_draft_only_during_dialog()
    {
        var t = new Rig();
        t.Conf.Hook = () => t.Settings.MeetingInviteMode = MeetingInviteMode.DraftOnly;
        await t.Send();
        Assert.Equal(InviteDecision.SaveDraft(InviteDraftReason.DraftOnlyMode), Assert.Single(t.Cal.Creates).Decision);
    }

    [Theory]
    [InlineData(MeetingInviteMode.Send)]
    [InlineData(MeetingInviteMode.DraftOnly)]
    public async Task Unresolved(MeetingInviteMode mode)
    {
        var t = new Rig();
        t.Settings.MeetingInviteMode = mode;
        t.Cal.Resolved = t.Cal.Resolved with { Unresolved = ["Nobody <nobody@x.org>"] };
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Send());
        Assert.Equal("These recipients could not be resolved in Outlook: Nobody <nobody@x.org>. Nothing was created. Check the addresses and try again.", ex.Message);
        Assert.Empty(t.Conf.Calls);
        Assert.Empty(t.Cal.Creates);
        Assert.StartsWith("Invite failed: These recipients", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Resolve_throws_propagates()
    {
        var t = new Rig();
        t.Cal.ResolveThrows = new McpException("Mailbox \"x@y.org\" is not configured.");
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Send());
        Assert.Equal("Mailbox \"x@y.org\" is not configured.", ex.Message);
        Assert.Empty(t.Conf.Calls);
        Assert.Empty(t.Cal.Creates);
        Assert.StartsWith("Invite failed:", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Create_throws_propagates()
    {
        var t = new Rig();
        t.Cal.CreateThrows = new McpException(InviteTexts.NoCalendarRights("shared@x.org"));
        var ex = await Assert.ThrowsAsync<McpException>(() => t.Send(mailbox: "shared@x.org"));
        Assert.Equal(InviteTexts.NoCalendarRights("shared@x.org"), ex.Message);
        Assert.Empty(t.Shown);
        var e = t.OneEvent();
        Assert.StartsWith("Invite failed:", e.Summary);
        Assert.Equal("shared@x.org", e.Mailbox);
    }

    [Fact]
    public async Task Shared_mailbox()
    {
        var t = new Rig();
        var r = await t.Send(mailbox: " Shared@X.org ");
        Assert.Equal("shared@x.org", t.Cal.Resolves[0].Mailbox);
        Assert.Equal("shared@x.org", t.Cal.Creates[0].Req.Mailbox);
        Assert.EndsWith("from the shared@x.org calendar.", r.Message);
        Assert.Equal("shared@x.org", r.Mailbox);
        Assert.Equal("shared@x.org", t.OneEvent().Mailbox);
    }

    [Fact]
    public async Task Teams_requested_not_added()
    {
        var t = new Rig();
        var r = await t.Send(teams: true);
        Assert.False(r.Teams);
        Assert.Equal(InviteTexts.TeamsUnavailable, r.Note);
        Assert.EndsWith(" " + InviteTexts.TeamsUnavailable, r.Message);
        var p = Assert.Single(t.Conf.Calls).Preview;
        Assert.True(p.TeamsRequested);
        Assert.False(p.TeamsAvailable);
    }

    [Fact]
    public async Task Teams_added()
    {
        var t = new Rig();
        t.Cal.TeamsAdded = true;
        var r = await t.Send(teams: true);
        Assert.True(r.Teams);
        Assert.Null(r.Note);
    }

    [Fact]
    public async Task Teams_not_requested()
    {
        var t = new Rig();
        var r = await t.Send(teams: false);
        Assert.False(r.Teams);
        Assert.Null(r.Note);
    }

    [Fact]
    public async Task Cancellation_during_dialog()
    {
        var t = new Rig();
        t.Conf.Throws = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Send());
        Assert.Empty(t.Cal.Creates);
        Assert.Equal("Invite abandoned: the request was cancelled", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Decision_is_honored_after_cancellation()
    {
        var t = new Rig();
        using var cts = new CancellationTokenSource();
        await t.Send(ct: cts.Token);
        Assert.False(Assert.Single(t.Cal.Creates).Cancelable);
    }

    [Fact]
    public async Task Validation_error_in_send_mode()
    {
        var t = new Rig();
        await Assert.ThrowsAsync<McpException>(() => t.Send(start: "2026-11-17"));
        Assert.Empty(t.Cal.Resolves);
        Assert.StartsWith("Invite failed:", t.OneEvent().Summary);
    }

    [Fact]
    public async Task Body_never_in_events()
    {
        var t = new Rig();
        await t.Send(body: "SECRET-BODY");
        Assert.DoesNotContain(t.Hub.GetEvents(), e => e.Summary.Contains("SECRET-BODY"));
    }

    [Fact]
    public async Task Long_subject_truncated_in_event()
    {
        var t = new Rig();
        var subject = new string('s', 120);
        await t.Send(subject: subject);
        Assert.Contains(TrayText.Truncate(subject, 80), t.OneEvent().Summary);
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

    [Fact]
    public void Invite_texts()
    {
        Assert.Equal("your calendar", InviteTexts.Calendar(""));
        Assert.Equal("the a@b.org calendar", InviteTexts.Calendar("a@b.org"));
        Assert.Contains("a@x.org; B <b@x.org>", InviteTexts.Unresolved(["a@x.org", "B <b@x.org>"]));
        Assert.Equal("The user doesn't have delegate or editor rights on the calendar of s@x.org, so the meeting can't be organized from it. Ask the user to get editor or delegate access from the mailbox owner, or leave mailbox empty to organize it from their own calendar.",
            InviteTexts.NoCalendarRights("s@x.org"));
    }
}
