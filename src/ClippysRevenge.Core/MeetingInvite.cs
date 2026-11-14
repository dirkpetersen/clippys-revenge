using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>One resolved meeting attendee.</summary>
public sealed record InviteAttendee(string Name, string Email, bool External);

/// <summary>A validated create_meeting_draft request (SPEC §22.1).</summary>
public sealed partial record MeetingRequest(
    IReadOnlyList<Recipient> Recipients,
    string Subject,
    DateTime Start, DateTime End,
    string Body,
    string? Location,
    string Mailbox)
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyForm();

    [GeneratedRegex(@" {2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    private const string DateFmt = "yyyy-MM-dd HH:mm";

    /// <summary>Throws McpException (only) for bad input.</summary>
    public static MeetingRequest Create(string? recipients, string? subject, string? start, string? end,
        string? body, string? location, string? mailbox, TimeZoneInfo tz, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tz);

        IReadOnlyList<Recipient> parsed;
        try
        {
            parsed = Core.Recipients.Parse(recipients ?? "");
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }
        if (parsed.Count == 0)
            throw new McpException("recipients is empty. Pass at least one attendee as \"Name <email>\" or a bare email, separated by \";\".");

        var subj = Spaces().Replace((subject ?? "").Replace('\r', ' ').Replace('\n', ' '), " ").Trim();
        if (subj.Length == 0)
            throw new McpException("subject is empty. Give the meeting a subject.");

        var s = ParseLocal("start", start, "2026-03-01T14:30", "the start of a find_free_times suggestion", tz);
        var e = ParseLocal("end", end, "2026-03-01T15:30", "the end of a find_free_times suggestion", tz);
        if (e <= s)
            throw new McpException($"end ({e.ToString(DateFmt, CultureInfo.InvariantCulture)}) must be after start ({s.ToString(DateFmt, CultureInfo.InvariantCulture)}).");

        var nowLocal = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        if (e <= nowLocal)
            throw new McpException($"This meeting ends in the past ({e.ToString(DateFmt, CultureInfo.InvariantCulture)}). Pick a future time.");

        var normBody = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        var loc = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        return new MeetingRequest(parsed, subj, s, e, normBody, loc, MailboxIndexes.Normalize(mailbox));
    }

    private static DateTime ParseLocal(string name, string? text, string example, string emptyHint, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpException($"{name} is empty. Pass a local date-time like {example}, or {emptyHint}.");
        var t = text.Trim();
        var shown = t.Length > 100 ? t[..100] : t;
        var invalid = new McpException(
            $"{name} \"{shown}\" is not a valid date-time. Use an ISO date-time like 2026-03-01T14:30 (local time), or the start and end of a find_free_times suggestion.");
        if (DateOnlyForm().IsMatch(t)) throw invalid;
        try
        {
            var dto = SearchDates.ParseFrom(t, tz) ?? throw invalid;
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(dto, tz).DateTime, DateTimeKind.Unspecified);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw invalid;
        }
    }
}

public sealed record ResolvedInvite(
    string OrganizerName, string OrganizerEmail,
    IReadOnlyList<InviteAttendee> Attendees,
    IReadOnlyList<string> Unresolved);

/// <summary>The saved meeting's ids. Empty strings when Outlook saved it but the ids couldn't be read.</summary>
public sealed record CreatedMeeting(string EntryId, string StoreId);

public interface IMeetingCalendar
{
    /// <summary>COM, Interactive. Validates the mailbox (throws McpException) and resolves recipients. Creates nothing.</summary>
    Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct);

    /// <summary>COM, Interactive. Creates the meeting and saves it unsent; never sends. Throws McpException
    /// for unresolved recipients (nothing saved) or missing calendar rights.</summary>
    Task<CreatedMeeting> CreateAsync(MeetingRequest request, CancellationToken ct);
}

public static class AttendeeKinds
{
    /// <summary>Outlook OlAddressEntryUserType 0..4 (Exchange user, DL, public folder, agent, organization) are internal.</summary>
    public static bool IsInternal(int olAddressEntryUserType) => olAddressEntryUserType is >= 0 and <= 4;
}

public static class InviteTexts
{
    public const string ToolDescription =
        "Saves a meeting unsent in the calendar. Nothing is sent: Clippy can't add a Teams or Zoom link, so the user opens the meeting in Outlook, adds the link and sends the invite there. " +
        "Times are local. To book option N of a find_free_times result, or of the times offered in a draft, pass that option's start and end; " +
        "the option lines show a date without a year, so use the next occurrence of that date. " +
        "Every recipient must resolve in Outlook or nothing is created. " +
        "With mailbox, the meeting is saved in that shared mailbox's calendar (delegate or editor rights needed).";

    public const string DisabledMessage =
        "Meeting drafts are disabled in Clippy's Revenge. Ask the user to enable them in the tray settings (Meeting invites: Draft only), then try again.";

    public const string DisabledReason = "meeting drafts are disabled";
    public const string CancelledReason = "the request was cancelled";

    public static string Unresolved(IEnumerable<string> entries) =>
        $"These recipients could not be resolved in Outlook: {string.Join("; ", entries)}. Nothing was created. Check the addresses and try again.";

    public static string NoCalendarRights(string smtp) =>
        $"The user doesn't have delegate or editor rights on the calendar of {smtp}, so the meeting can't be organized from it. " +
        "Ask the user to get editor or delegate access from the mailbox owner, or leave mailbox empty to organize it from their own calendar.";

    /// <summary>"your calendar" for "", else "the calendar of &lt;mailbox&gt;".</summary>
    public static string Calendar(string mailbox) => mailbox.Length == 0 ? "your calendar" : $"the calendar of {mailbox}";

    public static string Saved(string mailbox) =>
        $"The meeting was saved in {Calendar(mailbox)} without sending. It has no Teams or Zoom link: the user adds one in Outlook and sends the invite. It is listed in the Clippy tray under Draft invites.";

    /// <summary>"ddd M/d HH:mm–HH:mm", invariant culture, U+2013.</summary>
    public static string When(DateTime start, DateTime end) =>
        string.Create(CultureInfo.InvariantCulture, $"{start:ddd} {start:M/d} {start:HH:mm}–{end:HH:mm}");

    public static string SavedEvent(int attendees, DateTime start, DateTime end) =>
        $"meeting draft saved for {attendees} attendee{(attendees == 1 ? "" : "s")}: {When(start, end)}";

    public static string NotCreatedEvent(string reason) => $"meeting draft not created: {reason}";
}

public sealed record MeetingDraftResult
{
    [JsonPropertyName("status")] public string Status { get; init; } = "saved_as_draft";
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("subject")] public required string Subject { get; init; }
    [JsonPropertyName("start")] public required string Start { get; init; }
    [JsonPropertyName("end")] public required string End { get; init; }
    [JsonPropertyName("mailbox")] public required string Mailbox { get; init; }
    [JsonPropertyName("organizer")] public required string Organizer { get; init; }
    [JsonPropertyName("recipients")] public required string Recipients { get; init; }
    /// <summary>Always written, also as null (M15.2 sets it).</summary>
    [JsonPropertyName("category"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Category { get; init; }
    [JsonPropertyName("note_draft_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? NoteDraftId { get; init; }
    [JsonPropertyName("note_error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? NoteError { get; init; }
    /// <summary>Not serialized. The created meeting, for tracking (M15.2) and tests.</summary>
    [JsonIgnore] public string EntryId { get; init; } = "";
    [JsonIgnore] public string StoreId { get; init; } = "";
}
