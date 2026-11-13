using System.ComponentModel;
using System.Text.Json.Serialization;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

[McpServerToolType]
internal sealed partial class McpTools(ActivityHub hub, OutlookWorker worker)
{
    [McpServerTool(Name = "search_emails", ReadOnly = true, Idempotent = true)]
    [Description("Searches the local email index (fast, works while Outlook is busy). All filters are optional and combine with AND; results are newest first. Each result has id, mailbox, folder, date (local time), from, sender_email, to, subject, has_attachments, attachments[] (name, size, inline flag) and snippet. Pass a result's id to get_message to read the full body. Only synced mail is indexed; use index_status to check coverage if something seems missing.")]
    public IReadOnlyList<SearchResultDto> SearchEmails(
        Optional<MailboxIndexes> index,
        [Description("Free text over subject, body, sender, recipients and attachment file names. Words are ANDed; use OR between words for alternatives, \"quotes\" for a phrase, and a trailing * for a prefix.")] string? query = null,
        [Description("Substring of the sender's name or email address.")] string? sender = null,
        [Description("Substring of To or CC. Also accepts a list like 'Ann <ann@x.edu>; bob@x.edu', which matches mail involving any of them.")] string? recipient = null,
        [Description("Substring of the subject.")] string? subject = null,
        [Description("Earliest date, inclusive: ISO date (2026-03-01) or date-time (2026-03-01T14:30), local time.")] string? date_from = null,
        [Description("Latest date, inclusive. A date-only value includes the whole day.")] string? date_to = null,
        [Description("'inbox', 'sent', or another indexed folder path. Subfolders are not included. Empty = all folders.")] string? folder = null,
        [Description("true = only mail with real (non-inline) attachments, false = only mail without.")] bool? has_attachment = null,
        [Description("Substring of any attachment file name.")] string? attachment_name = null,
        [Description("Exact attachment extension, with or without the dot (pdf or .pdf).")] string? attachment_ext = null,
        [Description("true = only unread, false = only read.")] bool? unread = null,
        [Description("Empty = the user's own mailbox, an SMTP address = that shared mailbox, 'all' = every enabled mailbox.")] string? mailbox = null,
        [Description("Maximum results, 1 to 200.")] int limit = 50,
        [Description("Number of results to skip, for paging.")] int offset = 0) =>
        MailQueries.Search(index.Required(), TimeZoneInfo.Local, query, sender, recipient, subject, date_from, date_to,
            folder, has_attachment, attachment_name, attachment_ext, unread, mailbox, limit, offset);

    [McpServerTool(Name = "get_message", ReadOnly = true, Idempotent = true)]
    [Description("Returns one email in full (headers, complete body, attachment list) by the id from search_emails. Reads the index first; if the email is not indexed yet, reads it live from Outlook, which needs Outlook to be running. The mailbox is found from the id.")]
    public async Task<MessageDto> GetMessage(
        Optional<MailboxIndexes> index,
        Optional<OutlookMail> outlookMail,
        OutlookStores stores,
        [Description("The id of an email, as returned by search_emails.")] string id,
        CancellationToken cancellationToken = default)
    {
        var tz = TimeZoneInfo.Local;
        var mail = outlookMail.Required();
        if (MailQueries.GetIndexed(index.Required(), tz, id) is { } hit)
        {
            return hit;
        }
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, cancellationToken).ConfigureAwait(false);
        var live = await worker.RunAsync(app => mail.ReadById(app, id), WorkPriority.Interactive, cancellationToken)
            .ConfigureAwait(false);
        return MailQueries.FromLive(live, tz);
    }

    [McpServerTool(Name = "index_status", ReadOnly = true, Idempotent = true)]
    [Description("Shows the state of the local email index: per-mailbox counts, oldest and newest indexed mail, last sync and whether the initial backfill is complete, plus whether Outlook is running and what Clippy is doing right now. Use it to explain why a search found nothing or to check indexing progress.")]
    public IndexStatusDto IndexStatus(
        Optional<MailboxIndexes> index,
        [Description("Optional SMTP address of a shared mailbox. Empty = all configured mailboxes.")] string? mailbox = null)
    {
        bool? running = worker.State switch
        {
            OutlookConnectionState.Available or OutlookConnectionState.NotResponding => true,
            OutlookConnectionState.Unavailable => false,
            _ => null,
        };
        return MailQueries.Status(index.Value, hub, TimeZoneInfo.Local, running, mailbox);
    }

    [McpServerTool(Name = "list_events", ReadOnly = true, Idempotent = true)]
    [Description("Lists calendar events (recurring ones expanded into instances) in a time window, sorted by start, at most 500. Defaults to the next 7 days from today. Use mailbox for a shared mailbox's calendar.")]
    public async Task<ListEventsResult> ListEvents(
        OutlookStores stores,
        [Description("Window start: ISO date (2026-03-01) or date-time (2026-03-01T14:30, local time). Default: today 00:00.")] string? start = null,
        [Description("Window end: ISO date (covers the whole day) or date-time. Default: start + days.")] string? end = null,
        [Description("Window length in days when end is not given (1-366).")] int days = CalendarEvents.DefaultDays,
        [Description("Optional SMTP address of a shared mailbox whose calendar to read. Empty = your own.")] string? mailbox = null,
        CancellationToken ct = default)
    {
        var tz = TimeZoneInfo.Local;
        var window = CalendarEvents.ResolveWindow(start, end, days, tz, DateTimeOffset.Now);
        if (!string.IsNullOrWhiteSpace(mailbox))
            await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        var events = await worker.RunAsync(
            app => OutlookCalendar.ListEvents(app, stores, mailbox, window, tz), WorkPriority.Interactive, ct).ConfigureAwait(false);
        return new ListEventsResult(
            window.Start, window.End, events.Count, events.Count >= CalendarEvents.MaxEvents,
            events.Select(e => new EventResult(e.Id, e.Subject, e.Start, e.End, e.AllDay, e.Location, e.Organizer, e.IsRecurring, e.BusyStatus)).ToList());
    }

    private const string FindFreeTimesDescription =
        "Finds meeting times when everyone is free, from Outlook free/busy. Paste recipients as \"Last, First <email>; ...\" (separate with \";\"). Returns numbered suggestions with start/end (local time, ISO with offset) and a ready-to-show \"text\". People whose free/busy can't be read (e.g. outside the organization) are listed in unknown_attendees and ignored. Nothing is booked. To book suggestion N, pass its start and end to send_meeting_invite.";

#pragma warning disable IDE1006
    [McpServerTool(Name = "find_free_times", ReadOnly = true, Idempotent = true)]
    [Description(FindFreeTimesDescription)]
    public Task<FreeTimesResult> FindFreeTimes(
        FreeTimeFinder finder,
        Settings settings,
        [Description("Attendees as \"Name <email>; Name <email>\". Split on \";\" only.")] string recipients,
        [Description("Minimum free block length, 1-1440.")] int duration_minutes = 60,
        [Description("Calendar days to search, starting with date_from's day (max 60).")] int days_ahead = 10,
        [Description("Start date or date-time, local time (ISO). Default: now.")] string? date_from = null,
        [Description("Working-hours window, 0-23. Default: tray settings.")] int? earliest_hour = null,
        [Description("Working-hours window, 1-24. Default: tray settings.")] int? latest_hour = null,
        [Description("Skip Saturdays and Sundays. Default: tray settings.")] bool? business_days_only = null,
        [Description("Also check the calendar owner (you, or the shared mailbox).")] bool include_me = true,
        [Description("1-20.")] int max_suggestions = 5,
        [Description("Empty = your calendar; or the SMTP address of an enabled shared mailbox.")] string? mailbox = null,
        CancellationToken cancellationToken = default)
        => finder.FindAsync(new FreeTimeRequest(recipients, duration_minutes, days_ahead, date_from,
               earliest_hour, latest_hour, business_days_only, include_me, max_suggestions, mailbox),
               settings, cancellationToken);
#pragma warning restore IDE1006

    private const string SendMeetingInviteDescription =
        "Creates a meeting and sends the invite to the attendees. This is the only tool that can send anything. In Send mode the user is asked to confirm in a dialog, and the invite goes out immediately once they click Send; they may instead save it as a draft or cancel. If the result is \"cancelled\", do not retry unless the user asks. In Draft only mode the meeting is saved in the calendar unsent and the user sends it from Outlook. Times are local; pass the start and end from find_free_times. Every recipient must resolve in Outlook or nothing is created.";

    [McpServerTool(Name = "send_meeting_invite", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(SendMeetingInviteDescription)]
    public Task<MeetingInviteResult> SendMeetingInvite(
        MeetingInvites invites,
        [Description("Attendees as \"Name <email>; Name <email>\" or bare emails, separated by \";\" (never \",\"). Paste the list as given.")] string recipients,
        [Description("Meeting subject.")] string subject,
        [Description("Local start date-time, e.g. 2026-03-01T14:30, or the start of a find_free_times suggestion.")] string start,
        [Description("Local end date-time, e.g. 2026-03-01T15:30, or the end of a find_free_times suggestion.")] string end,
        [Description("Optional plain-text invite body.")] string? body = null,
        [Description("Optional location, e.g. a room.")] string? location = null,
        [Description("Accepted for compatibility, but no Teams link is ever added (Outlook has no supported API for it); the result has teams=false and a note telling the user to click Teams Meeting in Outlook if they need one.")] bool teams = false,
        [Description("Optional SMTP address of a configured shared mailbox to organize the meeting from. Empty = the user's own calendar.")] string? mailbox = null,
        CancellationToken cancellationToken = default)
        => invites.SendAsync(recipients, subject, start, end, body, location, teams, mailbox, cancellationToken);
}

public sealed record ListEventsResult(
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("end")] DateTimeOffset End,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("events")] IReadOnlyList<EventResult> Events);

public sealed record EventResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("end")] DateTimeOffset End,
    [property: JsonPropertyName("all_day")] bool AllDay,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("organizer")] string Organizer,
    [property: JsonPropertyName("is_recurring")] bool IsRecurring,
    [property: JsonPropertyName("busy_status")] string BusyStatus);
