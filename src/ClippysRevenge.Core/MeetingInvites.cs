using System.Globalization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>The create_meeting_draft flow (SPEC §22.1), behind IMeetingCalendar. Never sends.</summary>
public sealed class MeetingInvites(IMeetingCalendar calendar, Func<Settings> settings, ActivityHub hub,
    TimeZoneInfo timeZone, TimeProvider? time = null, MeetingNotes? notes = null)
{
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:sszzz";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed class Ctx
    {
        public string? Mailbox;
        public bool Recorded;
    }

    /// <exception cref="McpException">Disabled, bad arguments, unknown reply_to_id (ItemNotFoundException),
    /// reply_to_id while the index is unavailable, unresolved recipients, no calendar rights.</exception>
    /// <exception cref="OperationCanceledException">ct fired before the meeting was created.</exception>
    public async Task<MeetingDraftResult> CreateDraftAsync(string? recipients, string? subject, string? start, string? end,
        string? body, string? location, string? mailbox, string? replyToId, CancellationToken ct)
    {
        var s = settings();
        if (s.MeetingInviteMode != MeetingInviteMode.DraftOnly)
        {
            hub.Event(ActivityKind.Invite, InviteTexts.NotCreatedEvent(InviteTexts.DisabledReason));
            throw new McpException(InviteTexts.DisabledMessage);
        }

        var ctx = new Ctx();
        try
        {
            return await RunAsync(recipients, subject, start, end, body, location, mailbox, replyToId, s, ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ctx.Recorded)
                hub.Event(ActivityKind.Invite, InviteTexts.NotCreatedEvent(InviteTexts.CancelledReason), mailbox: ctx.Mailbox);
            throw;
        }
        catch (Exception ex)
        {
            if (!ctx.Recorded)
                hub.Event(ActivityKind.Invite, InviteTexts.NotCreatedEvent(TrayText.Truncate(ex.Message, 200)), mailbox: ctx.Mailbox);
            throw;
        }
    }

    private async Task<MeetingDraftResult> RunAsync(string? recipients, string? subject, string? start, string? end,
        string? body, string? location, string? mailbox, string? replyToId, Settings s, Ctx ctx, CancellationToken ct)
    {
        var req = MeetingRequest.Create(recipients, subject, start, end, body, location, mailbox, timeZone, _time.GetUtcNow());
        ctx.Mailbox = req.Mailbox.Length > 0 ? req.Mailbox : null;

        var replyTo = string.IsNullOrWhiteSpace(replyToId) ? null : replyToId.Trim();
        if (replyTo is not null)
        {
            if (notes is null) throw new McpException(Optional<MeetingNotes>.IndexUnavailableMessage);
            await notes.CheckAsync(replyTo, ct).ConfigureAwait(false);
        }

        var r = await calendar.ResolveAsync(req, ct).ConfigureAwait(false);
        if (r.Unresolved.Count > 0) throw new McpException(InviteTexts.Unresolved(r.Unresolved));

        // Once resolution is done, creation runs to completion even if the caller cancels.
        var m = await calendar.CreateAsync(req, CancellationToken.None).ConfigureAwait(false);

        hub.Event(ActivityKind.Invite, InviteTexts.SavedEvent(r.Attendees.Count, req.Start, req.End), mailbox: ctx.Mailbox);
        ctx.Recorded = true;

        MeetingNoteOutcome? note = null;
        if (replyTo is not null)
        {
            var label = TimeZoneLabels.Resolve(s.TimeZoneLabel, timeZone, req.Start);
            note = await notes!.SaveAsync(replyTo, req, label, CancellationToken.None).ConfigureAwait(false);
        }

        return new MeetingDraftResult
        {
            Message = InviteTexts.Saved(req.Mailbox) + (note?.DraftId is not null ? MeetingNoteText.SavedSuffix : ""),
            NoteDraftId = note?.DraftId,
            NoteError = note?.Error,
            Subject = req.Subject,
            Start = Fmt(req.Start),
            End = Fmt(req.End),
            Mailbox = req.Mailbox,
            Organizer = Recipients.Format([new Recipient(r.OrganizerName, r.OrganizerEmail)]),
            Recipients = Recipients.Format(r.Attendees.Select(a => new Recipient(a.Name, a.Email))),
            Category = null,
            EntryId = m.EntryId,
            StoreId = m.StoreId,
        };
    }

    private string Fmt(DateTime local) =>
        new DateTimeOffset(local, timeZone.GetUtcOffset(local)).ToString(TimeFormat, CultureInfo.InvariantCulture);
}
