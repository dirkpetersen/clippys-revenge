using System.Globalization;
using System.Text.Json.Serialization;
using Dapper;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public enum StatsGroupBy { Sender, SenderDomain, Recipient, RecipientDomain, Folder, Month, Week, Weekday, Hour }

public enum StatsOrder { Count, Key }

/// <summary>One group (SPEC 11.11). First/Last are UTC. Name is non-null ("" when unknown) only for Sender and
/// Recipient, null for every other grouping.</summary>
public sealed record StatsGroup(string Key, string? Name, long Count, DateTimeOffset First, DateTimeOffset Last);

/// <summary>Total = matching emails (not recipient entries). Groups sorted by Key (ordinal), unique keys.</summary>
public sealed record StatsCounts(long Total, IReadOnlyList<StatsGroup> Groups);

public sealed record StatsRowDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
    [property: JsonPropertyName("count")] long Count,
    [property: JsonPropertyName("first")] string First,
    [property: JsonPropertyName("last")] string Last);

public sealed record EmailStatsDto(
    [property: JsonPropertyName("group_by")] string GroupBy,
    [property: JsonPropertyName("mailbox")] string Mailbox,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("distinct")] int Distinct,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("rows")] IReadOnlyList<StatsRowDto> Rows);

/// <summary>email_stats (SPEC 11.11): counts and names only, never subjects, bodies or attachment names.</summary>
public static class EmailStats
{
    public const int DefaultTop = 10;
    public const int MaxTop = 200;
    public const string UnknownKey = "(unknown)";
    public const string GroupByError =
        "group_by must be one of sender, sender_domain, recipient, recipient_domain, folder, month, week, weekday, hour.";
    public const string OrderError = "order must be count or key.";

    private static readonly string[] WireNames =
        ["sender", "sender_domain", "recipient", "recipient_domain", "folder", "month", "week", "weekday", "hour"];

    private static readonly string[] WeekdayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private const int CancelCheckEvery = 1024;

    /// <summary>Trimmed, OrdinalIgnoreCase against the nine wire names.</summary>
    /// <exception cref="McpException">GroupByError (also for null/blank).</exception>
    public static StatsGroupBy ParseGroupBy(string? text)
    {
        var t = text?.Trim();
        if (!string.IsNullOrEmpty(t))
            for (var i = 0; i < WireNames.Length; i++)
                if (WireNames[i].Equals(t, StringComparison.OrdinalIgnoreCase)) return (StatsGroupBy)i;
        throw new McpException(GroupByError);
    }

    /// <summary>null/blank → Count. Otherwise trimmed, OrdinalIgnoreCase "count" / "key".</summary>
    /// <exception cref="McpException">OrderError.</exception>
    public static StatsOrder ParseOrder(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return StatsOrder.Count;
        if (t.Equals("count", StringComparison.OrdinalIgnoreCase)) return StatsOrder.Count;
        if (t.Equals("key", StringComparison.OrdinalIgnoreCase)) return StatsOrder.Key;
        throw new McpException(OrderError);
    }

    /// <exception cref="ArgumentOutOfRangeException">undefined value.</exception>
    public static string WireName(StatsGroupBy groupBy)
    {
        var i = (int)groupBy;
        if (i < 0 || i >= WireNames.Length) throw new ArgumentOutOfRangeException(nameof(groupBy));
        return WireNames[i];
    }

    // ---- key helpers (pure) ----

    /// <summary>Trim + ToLowerInvariant; UnknownKey when blank.</summary>
    public static string SenderKey(string? senderEmail)
    {
        var t = senderEmail?.Trim();
        return string.IsNullOrEmpty(t) ? UnknownKey : t.ToLowerInvariant();
    }

    /// <summary>Lower-cased text after the last '@' of an address key; UnknownKey for UnknownKey, null, blank,
    /// no '@', or nothing after the last '@'.</summary>
    public static string DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address == UnknownKey) return UnknownKey;
        var at = address.LastIndexOf('@');
        if (at < 0 || at == address.Length - 1) return UnknownKey;
        var d = address[(at + 1)..].Trim();
        return d.Length == 0 ? UnknownKey : d.ToLowerInvariant();
    }

    /// <summary>The recipients of one email: To entries, then CC entries when includeCc. One entry per address, the
    /// first occurrence wins; emails lower-cased; invalid entries skipped.</summary>
    public static IReadOnlyList<Recipient> SplitRecipients(string? to, string? cc, bool includeCc)
    {
        var result = new List<Recipient>();
        HashSet<string>? seen = null;
        void Add(string? list)
        {
            if (string.IsNullOrEmpty(list)) return;
            foreach (var part in list.Split(';'))
            {
                if (!Recipients.TryParseEntry(part, out var r)) continue;
                var email = r.Email.ToLowerInvariant();
                seen ??= new HashSet<string>(StringComparer.Ordinal);
                if (seen.Add(email)) result.Add(new Recipient(r.Name, email));
            }
        }
        Add(to);
        if (includeCc) Add(cc);
        return result;
    }

    /// <summary>Month "yyyy-MM", Week "yyyy-Www" (ISOWeek year and week), Weekday "Mon".."Sun", Hour "00".."23",
    /// all from the local time of unixSeconds in zone. Invariant culture.</summary>
    public static string TimeKey(StatsGroupBy groupBy, long unixSeconds, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (groupBy is not (StatsGroupBy.Month or StatsGroupBy.Week or StatsGroupBy.Weekday or StatsGroupBy.Hour))
            throw new ArgumentOutOfRangeException(nameof(groupBy));
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone).DateTime;
        var inv = CultureInfo.InvariantCulture;
        switch (groupBy)
        {
            case StatsGroupBy.Month:
                return local.ToString("yyyy-MM", inv);
            case StatsGroupBy.Week:
                return ISOWeek.GetYear(local).ToString("D4", inv) + "-W" + ISOWeek.GetWeekOfYear(local).ToString("D2", inv);
            case StatsGroupBy.Weekday:
                return WeekdayNames[((int)local.DayOfWeek + 6) % 7];
            default:
                return local.Hour.ToString("D2", inv);
        }
    }

    // ---- one mailbox ----

    private sealed class GroupRow
    {
        public string? K { get; set; }
        public long N { get; set; }
        public long First { get; set; }
        public long Last { get; set; }
        public string? Newest { get; set; }
    }

    private sealed class RecipientRow
    {
        public long Id { get; set; }
        public long T { get; set; }
        public string? ToAddrs { get; set; }
        public string? CcAddrs { get; set; }
    }

    private sealed class Acc
    {
        public long Count;
        public long Min = long.MaxValue;
        public long Max = long.MinValue;
        public string? Name;
        public long NameT = long.MinValue;
        public long NameId = long.MinValue;

        public void Add(long n, long first, long last)
        {
            Count += n;
            if (first < Min) Min = first;
            if (last > Max) Max = last;
        }

        public void OfferName(string name, long t, long id)
        {
            if (Name is null || t > NameT || (t == NameT && id > NameId))
            {
                Name = name;
                NameT = t;
                NameId = id;
            }
        }
    }

    private static StatsCounts Finish(long total, Dictionary<string, Acc> map, bool names)
    {
        var groups = new List<StatsGroup>(map.Count);
        foreach (var (key, a) in map)
            groups.Add(new StatsGroup(key, names ? a.Name ?? "" : null, a.Count,
                DateTimeOffset.FromUnixTimeSeconds(a.Min), DateTimeOffset.FromUnixTimeSeconds(a.Max)));
        groups.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return new StatsCounts(total, groups);
    }

    private static Acc Get(Dictionary<string, Acc> map, string key)
    {
        if (!map.TryGetValue(key, out var a)) map[key] = a = new Acc();
        return a;
    }

    /// <summary>Counts one mailbox. filter.Limit/Offset are ignored. includeCc only matters for
    /// Recipient/RecipientDomain. Checks ct before the query and every 1,024 streamed rows.</summary>
    public static StatsCounts Count(EmailIndex index, SearchFilter filter, StatsGroupBy groupBy, bool includeCc,
        TimeZoneInfo zone, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(zone);
        WireName(groupBy);
        ct.ThrowIfCancellationRequested();

        var f = EmailIndex.BuildFilter(filter);
        using var conn = index.OpenConnection();
        var map = new Dictionary<string, Acc>(StringComparer.Ordinal);
        long total = 0;

        switch (groupBy)
        {
            case StatsGroupBy.Sender:
            case StatsGroupBy.SenderDomain:
            {
                var newest = groupBy == StatsGroupBy.Sender
                    ? ", max(printf('%020d|%020d|', e.event_time, e.id) || coalesce(e.sender_name, '')) AS Newest"
                    : "";
                var sql = "SELECT lower(trim(e.sender_email)) AS K, count(*) AS N, min(e.event_time) AS First, " +
                          "max(e.event_time) AS Last" + newest + " FROM " + f.From + f.Where + " GROUP BY K";
                foreach (var row in conn.Query<GroupRow>(sql, f.Parameters, buffered: false))
                {
                    var key = SenderKey(row.K);
                    if (groupBy == StatsGroupBy.SenderDomain) key = DomainOf(key);
                    var a = Get(map, key);
                    a.Add(row.N, row.First, row.Last);
                    total += row.N;
                    if (groupBy == StatsGroupBy.Sender && row.Newest is { Length: >= 42 } nw &&
                        long.TryParse(nw.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out var t) &&
                        long.TryParse(nw.AsSpan(21, 20), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        a.OfferName(nw[42..], t, id);
                    else if (groupBy == StatsGroupBy.Sender)
                        a.OfferName("", row.Last, 0);
                    ct.ThrowIfCancellationRequested();
                }
                return Finish(total, map, groupBy == StatsGroupBy.Sender);
            }
            case StatsGroupBy.Folder:
            {
                var sql = "SELECT e.folder AS K, count(*) AS N, min(e.event_time) AS First, max(e.event_time) AS Last FROM " +
                          f.From + f.Where + " GROUP BY e.folder";
                foreach (var row in conn.Query<GroupRow>(sql, f.Parameters, buffered: false))
                {
                    Get(map, row.K ?? "").Add(row.N, row.First, row.Last);
                    total += row.N;
                }
                return Finish(total, map, false);
            }
            case StatsGroupBy.Month:
            case StatsGroupBy.Week:
            case StatsGroupBy.Weekday:
            case StatsGroupBy.Hour:
            {
                var sql = "SELECT e.event_time AS T FROM " + f.From + f.Where;
                foreach (var t in conn.Query<long>(sql, f.Parameters, buffered: false))
                {
                    Get(map, TimeKey(groupBy, t, zone)).Add(1, t, t);
                    if ((++total & (CancelCheckEvery - 1)) == 0) ct.ThrowIfCancellationRequested();
                }
                return Finish(total, map, false);
            }
            default:
            {
                var cc = includeCc ? "e.cc_addrs" : "NULL";
                var sql = "SELECT e.id AS Id, e.event_time AS T, e.to_addrs AS ToAddrs, " + cc + " AS CcAddrs FROM " +
                          f.From + f.Where;
                var byDomain = groupBy == StatsGroupBy.RecipientDomain;
                HashSet<string>? domains = byDomain ? new HashSet<string>(StringComparer.Ordinal) : null;
                foreach (var row in conn.Query<RecipientRow>(sql, f.Parameters, buffered: false))
                {
                    if ((++total & (CancelCheckEvery - 1)) == 0) ct.ThrowIfCancellationRequested();
                    var rcpts = SplitRecipients(row.ToAddrs, row.CcAddrs, includeCc);
                    if (byDomain)
                    {
                        domains!.Clear();
                        foreach (var r in rcpts) domains.Add(DomainOf(r.Email));
                        foreach (var d in domains) Get(map, d).Add(1, row.T, row.T);
                    }
                    else
                    {
                        foreach (var r in rcpts)
                        {
                            var a = Get(map, r.Email);
                            a.Add(1, row.T, row.T);
                            a.OfferName(r.Name, row.T, row.Id);
                        }
                    }
                }
                return Finish(total, map, !byDomain);
            }
        }
    }

    /// <summary>mailbox=all merge in the given order (own first): totals and counts added, earliest First, latest
    /// Last, Name from the group with the strictly later Last (ties keep the earlier list entry).</summary>
    public static StatsCounts Merge(IReadOnlyList<StatsCounts> perMailbox)
    {
        ArgumentNullException.ThrowIfNull(perMailbox);
        long total = 0;
        var map = new Dictionary<string, StatsGroup>(StringComparer.Ordinal);
        foreach (var c in perMailbox)
        {
            total += c.Total;
            foreach (var g in c.Groups)
            {
                if (!map.TryGetValue(g.Key, out var cur)) { map[g.Key] = g; continue; }
                map[g.Key] = new StatsGroup(g.Key,
                    g.Last > cur.Last ? g.Name : cur.Name,
                    cur.Count + g.Count,
                    g.First < cur.First ? g.First : cur.First,
                    g.Last > cur.Last ? g.Last : cur.Last);
            }
        }
        var groups = map.Values.ToList();
        groups.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
        return new StatsCounts(total, groups);
    }

    /// <summary>Count: count descending then key ascending. Key: key ascending (ordinal); Weekday Mon..Sun.
    /// Returns a new sorted list.</summary>
    public static IReadOnlyList<StatsGroup> Sort(IEnumerable<StatsGroup> groups, StatsGroupBy groupBy, StatsOrder order)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var list = groups.ToList();
        if (order == StatsOrder.Count)
        {
            list.Sort((a, b) =>
            {
                var c = b.Count.CompareTo(a.Count);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
        }
        else if (groupBy == StatsGroupBy.Weekday)
        {
            list.Sort((a, b) =>
            {
                var c = Array.IndexOf(WeekdayNames, a.Key).CompareTo(Array.IndexOf(WeekdayNames, b.Key));
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
        }
        else
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        }
        return list;
    }

    /// <summary>Sort, take Math.Clamp(top, 1, MaxTop), format First/Last with MailQueries.Iso(t, zone).</summary>
    public static EmailStatsDto Shape(StatsCounts counts, StatsGroupBy groupBy, StatsOrder order, int top,
        string mailbox, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(zone);
        var n = Math.Clamp(top, 1, MaxTop);
        var rows = Sort(counts.Groups, groupBy, order)
            .Take(n)
            .Select(g => new StatsRowDto(g.Key, g.Name, g.Count, MailQueries.Iso(g.First, zone), MailQueries.Iso(g.Last, zone)))
            .ToList();
        var distinct = counts.Groups.Count;
        return new EmailStatsDto(WireName(groupBy), mailbox, counts.Total, distinct, distinct > n, rows);
    }

    /// <summary>The whole tool. Validation (group_by, order, dates) happens before any index is touched.</summary>
    public static EmailStatsDto Run(MailboxIndexes indexes, TimeZoneInfo zone, SearchArgs filters, string? groupBy,
        int top = DefaultTop, string? order = null, bool includeCc = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(filters);
        var g = ParseGroupBy(groupBy);
        var o = ParseOrder(order);
        var filter = filters.ToFilter(zone);

        var key = MailboxIndexes.Normalize(filters.Mailbox);
        StatsCounts counts;
        if (key != MailboxIndexes.AllMailboxes)
        {
            counts = Count(indexes.For(filters.Mailbox), filter, g, includeCc, zone, ct);
        }
        else
        {
            var parts = new List<StatsCounts>();
            foreach (var k in indexes.Mailboxes)
            {
                EmailIndex ix;
                try { ix = indexes.For(k); }
                catch (McpException) { continue; }
                parts.Add(Count(ix, filter, g, includeCc, zone, ct));
            }
            counts = Merge(parts);
        }
        return Shape(counts, g, o, top, key, zone);
    }
}
