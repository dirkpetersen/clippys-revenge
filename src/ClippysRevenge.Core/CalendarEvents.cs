using System.Globalization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>One calendar event or recurrence instance as list_events returns it (SPEC §11.7).</summary>
public sealed record CalendarEventInfo(
    string Id,
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string Location,
    string Organizer,
    bool IsRecurring,
    string BusyStatus);

/// <summary>The resolved window of a list_events call.</summary>
public sealed record EventWindow(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Pure logic behind list_events (SPEC §11.7): window, Restrict filter, cap.</summary>
public static class CalendarEvents
{
    public const int MaxEvents = 500;
    public const int DefaultDays = 7;
    public const int MaxDays = 366;

    /// <summary>
    /// Resolves the window. start defaults to local midnight today; end defaults to start + days.
    /// Dates use the same formats as search_emails (SearchDates). A date-only end covers that whole day.
    /// </summary>
    public static EventWindow ResolveWindow(string? start, string? end, int days, TimeZoneInfo tz, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tz);
        if (days < 1 || days > MaxDays)
            throw new McpException($"days must be between 1 and {MaxDays}.");

        DateTimeOffset s;
        try
        {
            s = SearchDates.ParseFrom(start, tz) ?? StartOfDay(now, tz);
        }
        catch (FormatException ex) { throw new McpException(ex.Message.Replace("date_from", "start")); }

        DateTimeOffset e;
        try
        {
            e = SearchDates.ParseTo(end, tz) ?? s.AddDays(days);
        }
        catch (FormatException ex) { throw new McpException(ex.Message.Replace("date_to", "end")); }

        if (e <= s) throw new McpException("end must be after start.");
        return new EventWindow(s, e);
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz).DateTime.Date;
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>
    /// A DASL date literal in local wall-clock time, always the same shape whatever the machine culture
    /// (never the user's short-date format).
    /// </summary>
    public static string DaslDate(DateTimeOffset value, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(value, tz).DateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Overlap filter for Items.Restrict: ends after the window start and starts before its end.</summary>
    public static string BuildFilter(EventWindow w, TimeZoneInfo tz) =>
        $"@SQL=\"urn:schemas:calendar:dtend\" > '{DaslDate(w.Start, tz)}' AND \"urn:schemas:calendar:dtstart\" < '{DaslDate(w.End, tz)}'";

    /// <summary>Converts a COM local DateTime (Kind ignored) to an offset in tz.</summary>
    public static DateTimeOffset FromLocal(DateTime local, TimeZoneInfo tz)
    {
        var d = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(d, tz.GetUtcOffset(d));
    }

    /// <summary>Maps OlBusyStatus to text.</summary>
    public static string BusyStatusName(int olBusyStatus) => olBusyStatus switch
    {
        0 => "free",
        1 => "tentative",
        2 => "busy",
        3 => "out_of_office",
        4 => "working_elsewhere",
        _ => "busy",
    };
}
