using System.ComponentModel;
using System.Text.Json.Serialization;
using ClippysRevenge.Core;
using ModelContextProtocol.Server;

namespace ClippysRevenge.App;

[McpServerToolType]
internal sealed class McpTools(ActivityHub hub)
{
    [McpServerTool(Name = "index_status", ReadOnly = true, Idempotent = true)]
    [Description("Shows the state of the local email index: per-mailbox counts and sync progress, whether Outlook is running, and what Clippy is doing right now. Use it to explain why a search found nothing or to check indexing progress.")]
    public IndexStatusResult IndexStatus(
        [Description("Optional SMTP address of a shared mailbox. Empty = all configured mailboxes.")] string? mailbox = null) =>
        new(hub.State.ToString(), null, [], "The email index is not available yet.");

    [McpServerTool(Name = "list_events", ReadOnly = true, Idempotent = true)]
    [Description("Lists calendar events (recurring ones expanded into instances) in a time window, sorted by start, at most 500. Defaults to the next 7 days from today. Use mailbox for a shared mailbox's calendar.")]
    public async Task<ListEventsResult> ListEvents(
        OutlookWorker worker,
        OutlookStores stores,
        [Description("Window start: ISO date (2026-03-01) or date-time (2026-03-01T14:30, local time). Default: today 00:00.")] string? start = null,
        [Description("Window end: ISO date (covers the whole day) or date-time. Default: start + days.")] string? end = null,
        [Description("Window length in days when end is not given (1-366).")] int days = CalendarEvents.DefaultDays,
        [Description("Optional SMTP address of a shared mailbox whose calendar to read. Empty = your own.")] string? mailbox = null,
        CancellationToken ct = default)
    {
        var tz = TimeZoneInfo.Local;
        var window = CalendarEvents.ResolveWindow(start, end, days, tz, DateTimeOffset.Now);
        var events = await worker.RunAsync(
            app => OutlookCalendar.ListEvents(app, stores, mailbox, window, tz), WorkPriority.Interactive, ct).ConfigureAwait(false);
        return new ListEventsResult(
            window.Start, window.End, events.Count, events.Count >= CalendarEvents.MaxEvents,
            events.Select(e => new EventResult(e.Id, e.Subject, e.Start, e.End, e.AllDay, e.Location, e.Organizer, e.IsRecurring, e.BusyStatus)).ToList());
    }
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

public sealed record IndexStatusResult(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("outlook_running")] bool? OutlookRunning,
    [property: JsonPropertyName("mailboxes")] IReadOnlyList<object> Mailboxes,
    [property: JsonPropertyName("note")] string? Note);
