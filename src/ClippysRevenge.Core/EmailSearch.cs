using System.Globalization;

namespace ClippysRevenge.Core;

/// <summary>search_emails filters for one mailbox (SPEC §11.1). Null/blank = no filter.</summary>
public sealed record SearchFilter
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public string? Query { get; init; }
    public string? Sender { get; init; }
    public string? Recipient { get; init; }
    public string? Subject { get; init; }
    public DateTimeOffset? DateFrom { get; init; }
    public DateTimeOffset? DateTo { get; init; }
    public string? Folder { get; init; }
    public bool? HasAttachment { get; init; }
    public string? AttachmentName { get; init; }
    public string? AttachmentExt { get; init; }
    public bool? Unread { get; init; }
    public int Limit { get; init; } = DefaultLimit;
    public int Offset { get; init; }
}

/// <summary>One search hit. Date is UTC (offset 0); the tool converts to local.</summary>
public sealed record EmailSummary(
    string Id,
    string Folder,
    DateTimeOffset Date,
    string From,
    string SenderEmail,
    string To,
    string Subject,
    bool HasAttachments,
    IReadOnlyList<AttachmentInfo> Attachments,
    string Snippet)
{
    public string Mailbox { get; init; } = "";
}

/// <summary>Parses the date_from / date_to strings of search_emails (SPEC §11.1).</summary>
public static class SearchDates
{
    private static readonly string[] Formats = BuildFormats();

    private static string[] BuildFormats()
    {
        var list = new List<string>();
        foreach (var sep in new[] { "T", " " })
        {
            foreach (var time in new[] { "HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF" })
            {
                var f = "yyyy-MM-dd" + (sep == "T" ? "'T'" : " ") + time;
                list.Add(f);
                list.Add(f + "'Z'");
                list.Add(f + "zzz");
            }
        }
        return list.ToArray();
    }

    public static DateTimeOffset? ParseFrom(string? text, TimeZoneInfo tz) => Parse(text, tz, "date_from", false);

    public static DateTimeOffset? ParseTo(string? text, TimeZoneInfo tz) => Parse(text, tz, "date_to", true);

    private static DateTimeOffset? Parse(string? text, TimeZoneInfo tz, string param, bool endOfDay)
    {
        ArgumentNullException.ThrowIfNull(tz);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        try
        {
            return ParseCore(t, tz, param, endOfDay);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Invalid(param, t);
        }
    }

    private static FormatException Invalid(string param, string t) => new(
        $"{param} \"{t}\" is not a valid date. Use an ISO date like 2026-03-01 or a date-time like 2026-03-01T14:30 (local time).");

    private static DateTimeOffset? ParseCore(string t, TimeZoneInfo tz, string param, bool endOfDay)
    {

        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            if (!endOfDay) return Local(day, tz);
            return Local(day.AddDays(1), tz).AddSeconds(-1);
        }

        var hasOffset = t.EndsWith('Z') || (t.Length > 6 && (t[^6] == '+' || t[^6] == '-') && t[^3] == ':');
        if (hasOffset)
        {
            if (DateTimeOffset.TryParseExact(t, Formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dto))
                return dto;
        }
        else if (DateTime.TryParseExact(t, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return Local(dt, tz);
        }

        throw Invalid(param, t);
    }

    private static DateTimeOffset Local(DateTime dt, TimeZoneInfo tz)
    {
        dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
    }
}
