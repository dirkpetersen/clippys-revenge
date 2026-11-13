using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

// Types of the confirmation queue (M8.3). Added here because M8.3 had not landed when M8.4 was written.
public sealed record InviteAttendee(string Name, string Email, bool IsExternal);

public sealed record InvitePreview(
    string OrganizerName, string OrganizerEmail, string Mailbox,
    string Subject, DateTime Start, DateTime End,
    string? Location, bool TeamsRequested, bool TeamsAvailable,
    IReadOnlyList<InviteAttendee> Attendees, string Body);

public sealed record InviteConfirmation(InviteChoice? Choice, bool TimedOut);

public interface IInviteConfirmer
{
    /// <summary>Throws OperationCanceledException when ct fires (dialog closed, nothing chosen).</summary>
    Task<InviteConfirmation> ConfirmAsync(InvitePreview preview, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A validated send_meeting_invite request (SPEC §11.9).</summary>
public sealed partial record MeetingRequest(
    IReadOnlyList<Recipient> Recipients,
    string Subject,
    DateTime Start, DateTime End,
    string Body,
    string? Location,
    bool Teams,
    string Mailbox)
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyForm();

    [GeneratedRegex(@" {2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    private const string DateFmt = "yyyy-MM-dd HH:mm";

    /// <summary>Throws McpException (only) for bad input.</summary>
    public static MeetingRequest Create(string? recipients, string? subject, string? start, string? end,
        string? body, string? location, bool teams, string? mailbox, TimeZoneInfo tz, DateTimeOffset now)
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
        return new MeetingRequest(parsed, subj, s, e, normBody, loc, teams, MailboxIndexes.Normalize(mailbox));
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
    IReadOnlyList<string> Unresolved,
    bool TeamsAvailable);

public sealed record CreatedMeeting(bool TeamsAdded);

public interface IMeetingCalendar
{
    /// <summary>COM, Interactive. Validates the mailbox (throws McpException) and resolves recipients. Creates nothing.</summary>
    Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct);

    /// <summary>COM, Interactive. decision.Action must be Send or SaveDraft; otherwise InvalidOperationException.</summary>
    Task<CreatedMeeting> CreateAsync(MeetingRequest request, InviteDecision decision, CancellationToken ct);
}

public static class AttendeeKinds
{
    /// <summary>Outlook OlAddressEntryUserType 0..4 (Exchange user, DL, public folder, agent, organization) are internal.</summary>
    public static bool IsInternal(int olAddressEntryUserType) => olAddressEntryUserType is >= 0 and <= 4;
}

public static class InviteTexts
{
    public const string Cancelled = "The user declined this invite. Do not retry unless they ask.";

    public const string TeamsUnavailable =
        "A Teams meeting was requested, but Clippy's Revenge can't add one through the Teams add-in on this PC, " +
        "so the invite has no Teams link. If one is needed, the user can open the meeting in Outlook and click Teams Meeting.";

    public static string Unresolved(IEnumerable<string> entries) =>
        $"These recipients could not be resolved in Outlook: {string.Join("; ", entries)}. Nothing was created. Check the addresses and try again.";

    public static string NoCalendarRights(string smtp) =>
        $"The user doesn't have delegate or editor rights on the calendar of {smtp}, so the meeting can't be organized from it. " +
        "Ask the user to get editor or delegate access from the mailbox owner, or leave mailbox empty to organize it from their own calendar.";

    public static string Calendar(string mailbox) => mailbox.Length == 0 ? "your calendar" : $"the {mailbox} calendar";

    public static string SentBalloon(int attendees) =>
        $"It looks like you're trying to schedule a meeting. I sent {attendees} invite{(attendees == 1 ? "" : "s")}.";

    public static string TimeoutBalloon(string subject) =>
        $"It looks like you stepped away. I saved \"{subject}\" as a draft — review it in Outlook.";
}

public sealed record MeetingInviteResult
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("subject"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Subject { get; init; }
    [JsonPropertyName("start"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Start { get; init; }
    [JsonPropertyName("end"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? End { get; init; }
    [JsonPropertyName("mailbox"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mailbox { get; init; }
    [JsonPropertyName("organizer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Organizer { get; init; }
    [JsonPropertyName("recipients"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Recipients { get; init; }
    [JsonPropertyName("teams"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Teams { get; init; }
    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; init; }
}
