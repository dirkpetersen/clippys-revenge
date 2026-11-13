using System.Globalization;

namespace ClippysRevenge.Core;

/// <summary>Which date orders a folder: ReceivedTime or SentOn.</summary>
public enum EventTimeField { Received, Sent }

/// <summary>One Table row. All times UTC, whole seconds.</summary>
public sealed record MailTableRow(
    string EntryId, DateTimeOffset LastModified, DateTimeOffset EventTime,
    bool Unread, string? InternetMessageId);

public sealed record MailEntryState(string EntryId, bool Unread);

/// <summary>HasMore: at least one more matching row exists after Rows.</summary>
public sealed record MailTablePage(IReadOnlyList<MailTableRow> Rows, bool HasMore);

public static class MailTable
{
    public const string EntryIdColumn = "EntryID";
    public const string UnreadColumn = "UnRead";
    public const string LastModifiedProp = "http://schemas.microsoft.com/mapi/proptag/0x30080040";      // PR_LAST_MODIFICATION_TIME
    public const string ReceivedProp = "http://schemas.microsoft.com/mapi/proptag/0x0E060040";          // PR_MESSAGE_DELIVERY_TIME
    public const string SentOnProp = "http://schemas.microsoft.com/mapi/proptag/0x00390040";            // PR_CLIENT_SUBMIT_TIME
    public const string InternetMessageIdProp = "http://schemas.microsoft.com/mapi/proptag/0x1035001F"; // PR_INTERNET_MESSAGE_ID
    public const string MessageClassProp = "http://schemas.microsoft.com/mapi/proptag/0x001A001F";      // PR_MESSAGE_CLASS
    public const string SortLastModified = "[LastModificationTime]";
    public const string SortReceived = "[ReceivedTime]";
    public const string SortSentOn = "[SentOn]";

    public static string EventTimeProp(EventTimeField field) => field switch
    {
        EventTimeField.Received => ReceivedProp,
        EventTimeField.Sent => SentOnProp,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    public static string EventTimeSort(EventTimeField field) => field switch
    {
        EventTimeField.Received => SortReceived,
        EventTimeField.Sent => SortSentOn,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    /// <summary>EntryID, LastModified, event time, UnRead, InternetMessageId, MessageClass.</summary>
    public static IReadOnlyList<string> Columns(EventTimeField field) =>
        [EntryIdColumn, LastModifiedProp, EventTimeProp(field), UnreadColumn, InternetMessageIdProp, MessageClassProp];

    /// <summary>True for "IPM.Note" or "IPM.Note.&lt;anything&gt;".</summary>
    public static bool IsIndexedClass(string? messageClass) =>
        messageClass is not null &&
        (messageClass.Equals("IPM.Note", StringComparison.OrdinalIgnoreCase) ||
         messageClass.StartsWith("IPM.Note.", StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps one GetArray row (values in Columns() order). Null when the row is not indexable.</summary>
    public static MailTableRow? ToRow(IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 6) throw new ArgumentException("A Table row has 6 values.", nameof(values));

        var id = (values[0] as string)?.Trim();
        if (string.IsNullOrEmpty(id)) return null;
        if (!IsIndexedClass(values[5] as string)) return null;
        var evt = OutlookDates.FromTableValue(values[2]);
        if (evt is null) return null;
        var lm = OutlookDates.FromTableValue(values[1]) ?? evt.Value;
        var imid = (values[4] as string)?.Trim();
        if (string.IsNullOrEmpty(imid)) imid = null;
        return new MailTableRow(id, lm, evt.Value, values[3] is bool b && b, imid);
    }
}

/// <summary>The single place DASL filters and their date literals are built (SPEC §8).</summary>
public static class DaslFilter
{
    public static readonly TimeSpan Margin = TimeSpan.FromDays(1);

    private const string NoteClass = "(\"" + MailTable.MessageClassProp + "\" LIKE 'IPM.Note%')";

    /// <summary>UTC, truncated to the minute, invariant culture. No quotes.</summary>
    public static string Literal(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string P(string prop) => "\"" + prop + "\"";

    public static string Changed(EventTimeField field, DateTimeOffset modifiedFrom, DateTimeOffset cutoff) =>
        $"@SQL=({P(MailTable.LastModifiedProp)} >= '{Literal(modifiedFrom - Margin)}') AND " +
        $"({P(MailTable.EventTimeProp(field))} >= '{Literal(cutoff - Margin)}') AND {NoteClass}";

    public static string Range(EventTimeField field, DateTimeOffset from, DateTimeOffset to) =>
        $"@SQL=({P(MailTable.EventTimeProp(field))} >= '{Literal(from - Margin)}') AND " +
        $"({P(MailTable.EventTimeProp(field))} < '{Literal(to + Margin)}') AND {NoteClass}";

    public static string Since(EventTimeField field, DateTimeOffset since) =>
        $"@SQL=({P(MailTable.EventTimeProp(field))} >= '{Literal(since - Margin)}') AND {NoteClass}";

    public static string ByMessageId(string internetMessageId)
    {
        if (string.IsNullOrWhiteSpace(internetMessageId))
            throw new ArgumentException("Internet message id is empty.", nameof(internetMessageId));
        return $"@SQL={P(MailTable.InternetMessageIdProp)} = '{internetMessageId.Replace("'", "''")}'";
    }
}

public static class OutlookDates
{
    private static readonly DateTime FileTimeZero = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static bool IsNone(DateTime dt) => dt.Year >= 4501 || dt <= FileTimeZero;

    /// <summary>Table value (namespace-referenced date column = UTC) -> UTC whole seconds, or null.</summary>
    public static DateTimeOffset? FromTableValue(object? value)
    {
        if (value is not DateTime dt || IsNone(dt)) return null;
        var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return TruncateToSecond(new DateTimeOffset(utc));
    }

    /// <summary>MailItem property (local wall time) -> UTC whole seconds, or null. Never throws.</summary>
    public static DateTimeOffset? FromLocal(DateTime value, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (IsNone(value)) return null;
        try
        {
            var wall = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            var utc = wall - zone.GetUtcOffset(wall);
            return TruncateToSecond(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Floors to whole seconds, converts to UTC offset 0.</summary>
    public static DateTimeOffset TruncateToSecond(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Ticks - u.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}

public static class MailTablePager
{
    public const int DefaultPageRows = 500;
    public const int MaxPageRows = 5000;

    /// <summary>Takes up to maxRows kept rows, never splitting a group of equal keys.</summary>
    public static MailTablePage Take(
        IEnumerable<MailTableRow> sortedRows, Func<MailTableRow, bool> keep,
        Func<MailTableRow, DateTimeOffset> key, bool descending, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(sortedRows);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRows, MaxPageRows);

        var page = new List<MailTableRow>();
        DateTimeOffset? previous = null;
        DateTimeOffset lastAdded = default;
        foreach (var row in sortedRows)
        {
            var k = key(row);
            if (previous is { } p && (descending ? k > p : k < p))
                throw new InvalidOperationException("Table rows are not sorted.");
            previous = k;
            if (!keep(row)) continue;
            if (page.Count >= maxRows && k != lastAdded) return new MailTablePage(page, true);
            page.Add(row);
            lastAdded = k;
        }
        return new MailTablePage(page, false);
    }
}

/// <summary>Raw GetArray rows -> exact results.</summary>
public static class MailTableQueries
{
    private static IEnumerable<MailTableRow> Map(IEnumerable<IReadOnlyList<object?>> raw)
    {
        foreach (var r in raw)
            if (MailTable.ToRow(r) is { } row) yield return row;
    }

    /// <summary>rawRows sorted ascending by LastModified.</summary>
    public static MailTablePage Changed(IEnumerable<IReadOnlyList<object?>> rawRows,
        DateTimeOffset modifiedFrom, DateTimeOffset cutoff, int maxRows = MailTablePager.DefaultPageRows)
    {
        ArgumentNullException.ThrowIfNull(rawRows);
        var mf = OutlookDates.TruncateToSecond(modifiedFrom);
        var c = OutlookDates.TruncateToSecond(cutoff);
        return MailTablePager.Take(Map(rawRows), r => r.LastModified >= mf && r.EventTime >= c,
            r => r.LastModified, false, maxRows);
    }

    /// <summary>rawRows sorted descending by EventTime. from inclusive, to exclusive.</summary>
    public static MailTablePage Range(IEnumerable<IReadOnlyList<object?>> rawRows,
        DateTimeOffset from, DateTimeOffset to, int maxRows = MailTablePager.DefaultPageRows)
    {
        ArgumentNullException.ThrowIfNull(rawRows);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRows, MailTablePager.MaxPageRows);
        var f = OutlookDates.TruncateToSecond(from);
        var t = OutlookDates.TruncateToSecond(to);
        if (f >= t) return new MailTablePage([], false);
        return MailTablePager.Take(Map(rawRows), r => r.EventTime >= f && r.EventTime < t,
            r => r.EventTime, true, maxRows);
    }

    /// <summary>Any order. Result keeps input order; duplicate entry ids keep the first.</summary>
    public static IReadOnlyList<MailEntryState> EntryIds(IEnumerable<IReadOnlyList<object?>> rawRows, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(rawRows);
        var s = OutlookDates.TruncateToSecond(since);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MailEntryState>();
        foreach (var row in Map(rawRows))
            if (row.EventTime >= s && seen.Add(row.EntryId))
                result.Add(new MailEntryState(row.EntryId, row.Unread));
        return result;
    }
}
