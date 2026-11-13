using System.Globalization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>The whole send_meeting_invite flow (SPEC §6.6, §11.9), behind fakeable interfaces.</summary>
public sealed class MeetingInvites(IMeetingCalendar calendar, IInviteConfirmer confirmer, Func<Settings> settings,
    ActivityHub hub, BalloonCenter balloons, TimeZoneInfo timeZone, TimeProvider? time = null)
{
    private const string RefusedSummary = "Invite refused: invites are disabled";
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:sszzz";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed class Ctx
    {
        public string? Mailbox;
        public bool Recorded;
    }

    public async Task<MeetingInviteResult> SendAsync(string? recipients, string? subject, string? start, string? end,
        string? body, string? location, bool teams, string? mailbox, CancellationToken ct)
    {
        var mode0 = settings().MeetingInviteMode;
        if (mode0 == MeetingInviteMode.Disabled)
        {
            var d = InviteGate.Decide(mode0, null, false);
            hub.Event(ActivityKind.Invite, RefusedSummary);
            throw new McpException(d.RefuseReason!);
        }

        var ctx = new Ctx();
        try
        {
            return await RunAsync(recipients, subject, start, end, body, location, teams, mailbox, mode0, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ctx.Recorded) hub.Event(ActivityKind.Invite, "Invite abandoned: the request was cancelled", mailbox: ctx.Mailbox);
            throw;
        }
        catch (Exception ex)
        {
            if (!ctx.Recorded)
                hub.Event(ActivityKind.Invite, "Invite failed: " + TrayText.Truncate(ex.Message, 200), mailbox: ctx.Mailbox);
            throw;
        }
    }

    private async Task<MeetingInviteResult> RunAsync(string? recipients, string? subject, string? start, string? end,
        string? body, string? location, bool teams, string? mailbox, MeetingInviteMode mode0, Ctx ctx, CancellationToken ct)
    {
        var req = MeetingRequest.Create(recipients, subject, start, end, body, location, teams, mailbox, timeZone, _time.GetUtcNow());
        ctx.Mailbox = req.Mailbox.Length > 0 ? req.Mailbox : null;
        var subj = TrayText.Truncate(req.Subject, 80);

        var r = await calendar.ResolveAsync(req, ct).ConfigureAwait(false);
        if (r.Unresolved.Count > 0) throw new McpException(InviteTexts.Unresolved(r.Unresolved));

        InviteDecision d;
        if (InviteGate.RequiresConfirmation(mode0))
        {
            var preview = new InvitePreview(r.OrganizerName, r.OrganizerEmail, req.Mailbox, req.Subject, req.Start, req.End,
                req.Location, req.Teams, r.TeamsAvailable, r.Attendees, req.Body);
            var c = await confirmer.ConfirmAsync(preview, TimeSpan.FromSeconds(settings().InviteConfirmTimeoutSeconds), ct).ConfigureAwait(false);
            var mode = MostRestrictive(mode0, settings().MeetingInviteMode);
            d = InviteGate.Decide(mode, c.Choice, c.TimedOut);
        }
        else
        {
            d = InviteGate.Decide(mode0, null, false);
        }

        switch (d.Action)
        {
            case InviteAction.Refuse:
                hub.Event(ActivityKind.Invite, RefusedSummary, mailbox: ctx.Mailbox);
                ctx.Recorded = true;
                throw new McpException(d.RefuseReason!);
            case InviteAction.Cancel:
                hub.Event(ActivityKind.Invite, $"Invite cancelled by the user: \"{subj}\"", mailbox: ctx.Mailbox);
                ctx.Recorded = true;
                return new MeetingInviteResult { Status = "cancelled", Message = InviteTexts.Cancelled };
            case InviteAction.Send:
            case InviteAction.SaveDraft:
                break;
            default:
                throw new InvalidOperationException("Unexpected invite action.");
        }

        // The decision is made: the caller's cancellation no longer aborts it.
        var m = await calendar.CreateAsync(req, d, CancellationToken.None).ConfigureAwait(false);

        var n = r.Attendees.Count;
        var s = n == 1 ? "" : "s";
        var cal = InviteTexts.Calendar(req.Mailbox);
        var note = req.Teams && !m.TeamsAdded ? InviteTexts.TeamsUnavailable : null;
        string status, message;
        if (d.Action == InviteAction.Send)
        {
            status = "sent";
            message = $"The invite was sent to {n} recipient{s} from {cal}.";
            hub.Event(ActivityKind.Invite, $"Invite sent: \"{subj}\" to {n} recipient{s}", mailbox: ctx.Mailbox);
            balloons.Post(InviteTexts.SentBalloon(n));
        }
        else
        {
            status = "saved_as_draft";
            message = d.DraftReason switch
            {
                InviteDraftReason.UserChoice =>
                    $"The user chose Save as draft. The meeting is saved unsent in {cal}; the user needs to review it in Outlook and send it from there.",
                InviteDraftReason.Timeout =>
                    $"Nobody answered the confirmation dialog within {settings().InviteConfirmTimeoutSeconds} seconds, so the meeting is saved unsent in {cal}. The user needs to review it in Outlook and send it from there.",
                _ =>
                    $"Meeting invites are set to Draft only, so the meeting is saved unsent in {cal}. The user needs to review it in Outlook and send it from there.",
            };
            hub.Event(ActivityKind.Invite, $"Invite saved as draft ({d.ReasonCode}): \"{subj}\"", mailbox: ctx.Mailbox);
            if (d.DraftReason == InviteDraftReason.Timeout) balloons.Post(InviteTexts.TimeoutBalloon(subj));
        }
        ctx.Recorded = true;
        if (note is not null) message += " " + note;

        return new MeetingInviteResult
        {
            Status = status,
            Reason = d.ReasonCode,
            Message = message,
            Subject = req.Subject,
            Start = Fmt(req.Start),
            End = Fmt(req.End),
            Mailbox = req.Mailbox,
            Organizer = Recipients.Format([new Recipient(r.OrganizerName, r.OrganizerEmail)]),
            Recipients = Recipients.Format(r.Attendees.Select(a => new Recipient(a.Name, a.Email))),
            Teams = m.TeamsAdded,
            Note = note,
        };
    }

    private string Fmt(DateTime local) =>
        new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static MeetingInviteMode MostRestrictive(MeetingInviteMode a, MeetingInviteMode b) =>
        Rank(a) <= Rank(b) ? a : b;

    private static int Rank(MeetingInviteMode m) => m switch
    {
        MeetingInviteMode.Disabled => 0,
        MeetingInviteMode.DraftOnly => 1,
        _ => 2,
    };
}
