using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public interface IFreeBusySource
{
    /// <summary>The signed-in user (own mailbox owner). Email is a primary SMTP address.</summary>
    Task<Recipient> GetCurrentUserAsync(WorkPriority priority, CancellationToken ct);

    /// <summary>Joined free/busy string starting at from.Date. Null = unknown attendee.</summary>
    Task<string?> GetFreeBusyAsync(string email, DateTime from, DateTime to, int intervalMinutes,
        WorkPriority priority, CancellationToken ct);
}

public sealed record FreeTimeRequest(
    string? Recipients,
    int DurationMinutes = 60,
    int DaysAhead = 10,
    string? DateFrom = null,
    int? EarliestHour = null,
    int? LatestHour = null,
    bool? BusinessDaysOnly = null,
    bool IncludeMe = true,
    int MaxSuggestions = 5,
    string? Mailbox = null,
    bool OfferCet = false);

public sealed record FreeTimeSuggestion(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("start")] string Start,
    [property: JsonPropertyName("end")] string End,
    [property: JsonPropertyName("adjacent")] string Adjacent);

public sealed record FreeTimeOffer(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("start")] string Start,
    [property: JsonPropertyName("end")] string End,
    [property: JsonPropertyName("adjacent")] string Adjacent,
    [property: JsonPropertyName("list")] string List);

public sealed record FreeTimeAttendee(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email);

public sealed record FreeTimesResult(
    [property: JsonPropertyName("suggestions")] IReadOnlyList<FreeTimeSuggestion> Suggestions,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("offers")] IReadOnlyList<FreeTimeOffer> Offers,
    [property: JsonPropertyName("offer_text")] string OfferText,
    [property: JsonPropertyName("unknown_attendees")] IReadOnlyList<string> UnknownAttendees,
    [property: JsonPropertyName("attendees")] IReadOnlyList<FreeTimeAttendee> Attendees);

public sealed class FreeTimeFinder(
    IFreeBusySource source,
    TimeProvider time,
    TimeZoneInfo timeZone,
    Func<TimeZoneInfo?>? centralEurope = null,
    ILogger? logger = null)
{
    public const int MaxDaysAhead = 60;
    public const int MaxAttendees = 50;
    public const int MaxSuggestionsCap = 20;
    public const int MaxDurationMinutes = 1440;

    public const string CetMissingWarning =
        "Meeting times: no Central European time zone (Europe/Berlin or W. Europe Standard Time) on this PC; offers have no CET times.";

    private const string IsoFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    private int cetWarned;

    public async Task<FreeTimesResult> FindAsync(FreeTimeRequest request, Settings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        // 1. Recipients
        IReadOnlyList<Recipient> parsed;
        try
        {
            parsed = Recipients.Parse(request.Recipients ?? "");
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }
        if (parsed.Count > MaxAttendees)
            throw new McpException($"find_free_times takes at most {MaxAttendees} attendees; got {parsed.Count}.");

        // 2. Duration
        var duration = request.DurationMinutes;
        if (duration is < 1 or > MaxDurationMinutes)
            throw new McpException("duration_minutes must be between 1 and 1440.");

        // 3. Hours
        var startHour = request.EarliestHour ?? settings.WorkStartHour;
        var endHour = request.LatestHour ?? settings.WorkEndHour;
        if (startHour is < 0 or > 23) throw new McpException("earliest_hour must be between 0 and 23.");
        if (endHour is < 1 or > 24) throw new McpException("latest_hour must be between 1 and 24.");
        if (endHour <= startHour) throw new McpException("latest_hour must be later than earliest_hour.");

        // 4. Clamps
        var days = Math.Clamp(request.DaysAhead, 1, MaxDaysAhead);
        var max = Math.Clamp(request.MaxSuggestions, 1, MaxSuggestionsCap);
        var businessDays = request.BusinessDaysOnly ?? settings.BusinessDaysOnly;
        var tentativeIsBusy = settings.TentativeIsBusy;
        var tzLabel = settings.TimeZoneLabel;

        // 5. Now and range
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), timeZone).DateTime;
        DateTimeOffset? parsedFrom;
        try
        {
            parsedFrom = SearchDates.ParseFrom(request.DateFrom, timeZone);
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }
        var from = parsedFrom is { } dto ? TimeZoneInfo.ConvertTime(dto, timeZone).DateTime : now;
        var to = from.Date.AddDays(days);

        // 6. Mailbox
        var key = MailboxIndexes.Normalize(request.Mailbox);
        SharedMailbox? shared = null;
        if (key.Length != 0)
        {
            shared = settings.FindSharedMailbox(key);
            if (shared is null || !shared.Enabled || key == MailboxIndexes.AllMailboxes)
            {
                var keys = new List<string> { "" };
                foreach (var sm in settings.SharedMailboxes)
                    if (sm is { Enabled: true }) keys.Add(sm.Smtp.Trim().ToLowerInvariant());
                throw MailboxIndexes.MailboxError(request.Mailbox, keys);
            }
        }

        // 7. Empty
        if (parsed.Count == 0 && !request.IncludeMe)
            throw new McpException(
                "find_free_times needs at least one attendee. Pass recipients as \"Name <email>; ...\" or set include_me=true.");

        // 8. Owner
        var attendees = new List<Recipient>(parsed);
        string? ownerEmail = null;
        if (request.IncludeMe)
        {
            var me = shared is null
                ? await source.GetCurrentUserAsync(WorkPriority.Interactive, ct).ConfigureAwait(false)
                : new Recipient(shared.DisplayName.Trim(), shared.Smtp.Trim());
            ownerEmail = me.Email;
            if (!attendees.Any(a => string.Equals(a.Email, me.Email, StringComparison.OrdinalIgnoreCase)))
                attendees.Add(me);
        }

        // 9. Free/busy
        var interval = FreeBusy.DefaultIntervalMinutes;
        var needed = (int)Math.Ceiling((to - from.Date).TotalMinutes / interval);
        var known = new List<Recipient>();
        var unknown = new List<string>();
        var busyLists = new List<IReadOnlyList<TimeRange>>();
        var owner = OwnerCalendar.Unknown;
        foreach (var a in attendees)
        {
            ct.ThrowIfCancellationRequested();
            var fb = await source.GetFreeBusyAsync(a.Email, from, to, interval, WorkPriority.Interactive, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(fb) || fb.Length < needed)
            {
                unknown.Add(a.Email);
                continue;
            }
            try
            {
                busyLists.Add(FreeBusy.BusyIntervals(fb, from.Date, interval, tentativeIsBusy));
                known.Add(a);
            }
            catch (FormatException)
            {
                unknown.Add(a.Email);
                continue;
            }
            if (ownerEmail is not null && string.Equals(a.Email, ownerEmail, StringComparison.OrdinalIgnoreCase))
                owner = OwnerCalendar.FromFreeBusy(fb, from.Date, interval);
        }

        // 10. Blocks (the whole list, ranked before max is applied)
        IReadOnlyList<TimeRange> blocks = known.Count == 0
            ? []
            : FreeBusy.FreeSlots(busyLists, from, to, startHour, endHour, businessDays, duration, now, interval);

        // 11. Suggestions
        var suggestions = new List<FreeTimeSuggestion>();
        var ranked = MeetingRanking.Rank(blocks, owner).Take(max).ToList();
        for (var i = 0; i < ranked.Count; i++)
        {
            var s = ranked[i].Range;
            var label = SlotFormatter.Format(i + 1, s.Start, s.End,
                TimeZoneLabels.Resolve(tzLabel, timeZone, s.Start), startHour, endHour);
            suggestions.Add(new FreeTimeSuggestion(i + 1, label, Iso(s.Start), Iso(s.End),
                MeetingRanking.Name(ranked[i].Tier)));
        }

        // 12. Offers
        var offers = new List<FreeTimeOffer>();
        var offerText = "";
        if (blocks.Count > 0)
        {
            var cet = request.OfferCet ? (centralEurope ?? SenderRegion.CentralEurope)() : null;
            var set = MeetingOffers.Build(blocks, owner, duration, max, timeZone, tzLabel,
                europe: request.OfferCet, centralEurope: cet);
            foreach (var o in set.All)
                offers.Add(new FreeTimeOffer(o.N, o.Line, Iso(o.Range.Start), Iso(o.Range.End),
                    MeetingRanking.Name(o.Tier), o.ListName));
            offerText = set.ToText();
            if (set.CetMissing && Interlocked.Exchange(ref cetWarned, 1) == 0)
                logger?.LogWarning("{Message}", CetMissingWarning);
        }

        // 13. Result
        string text;
        if (suggestions.Count > 0)
            text = string.Join("\n", suggestions.Select(s => s.Label));
        else if (known.Count == 0)
            text = "No free/busy information could be read for any attendee, so no times can be suggested. Check the addresses in unknown_attendees.";
        else
            text = $"No free time of at least {duration} minutes was found for everyone between "
                + $"{from.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} and "
                + $"{to.AddDays(-1).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. "
                + "Try a larger days_ahead, a shorter duration_minutes, or a wider earliest_hour/latest_hour.";

        return new FreeTimesResult(
            suggestions, text, offers, offerText, unknown,
            known.Select(a => new FreeTimeAttendee(a.Name, a.Email)).ToList());
    }

    private string Iso(DateTime x) =>
        new DateTimeOffset(x, timeZone.GetUtcOffset(x)).ToString(IsoFormat, CultureInfo.InvariantCulture);
}
