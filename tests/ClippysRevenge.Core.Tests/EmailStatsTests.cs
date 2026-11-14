using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-stats-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;

    private static readonly TimeZoneInfo UtcMinus8 = TimeZoneInfo.CreateCustomTimeZone("T-8", TimeSpan.FromHours(-8), "T-8", "T-8");
    private static readonly TimeZoneInfo UtcPlus2 = TimeZoneInfo.CreateCustomTimeZone("T+2", TimeSpan.FromHours(2), "T+2", "T+2");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public EmailStatsTests()
    {
        _ix = new EmailIndex(Path.Combine(_dir, "index.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DateTimeOffset U(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private static IndexedEmail Build(string id, DateTimeOffset t, string folder = "Inbox", string subject = "",
        string senderName = "", string senderEmail = "", string to = "", string cc = "", bool unread = false) => new()
    {
        EntryId = id,
        Folder = folder,
        Subject = subject,
        SenderName = senderName,
        SenderEmail = senderEmail,
        To = to,
        Cc = cc,
        Body = "",
        Unread = unread,
        EventTime = t,
        Attachments = [],
    };

    private static void Seed(EmailIndex ix, bool m5Unread = false)
    {
        // One by one so row ids ascend in insertion order.
        ix.UpsertBatch([Build("m1", U(2026, 3, 2, 10), "Inbox", "", "Alice Old", "Alice@Example.edu", "me@x.org")]);
        ix.UpsertBatch([Build("m2", U(2026, 5, 4, 9), "Inbox", "", "Example, Alice", "alice@example.edu ",
            "Me <me@x.org>; Bob <bob@x.org>", "carol@other.net")]);
        ix.UpsertBatch([Build("m3", U(2026, 5, 4, 9), "Sent Items", "", "Me", "me@x.org",
            "Alice <alice@example.edu>", "bob@x.org")]);
        ix.UpsertBatch([Build("m4", U(2026, 6, 1, 12), "Inbox\\Projects", "", "", "", "bad entry; dave@other.net")]);
        ix.UpsertBatch([Build("m5", U(2026, 6, 1, 12), "Inbox", "", "Bob", "bob@x.org", "", "", m5Unread)]);
    }

    private static Dictionary<string, StatsGroup> By(StatsCounts c) => c.Groups.ToDictionary(g => g.Key);

    private StatsCounts Count(StatsGroupBy g, SearchFilter? f = null, bool cc = true, TimeZoneInfo? z = null) =>
        EmailStats.Count(_ix, f ?? new SearchFilter(), g, cc, z ?? Utc);

    // ---- T1 ----

    [Fact]
    public void SenderKey_Normalizes()
    {
        Assert.Equal("alice@example.edu", EmailStats.SenderKey(" Alice@Example.EDU "));
        Assert.Equal("(unknown)", EmailStats.SenderKey(null));
        Assert.Equal("(unknown)", EmailStats.SenderKey(""));
        Assert.Equal("(unknown)", EmailStats.SenderKey("  "));
        Assert.Equal("änne@x.de", EmailStats.SenderKey("ÄNNE@x.de"));
    }

    [Theory]
    [InlineData("alice@example.edu", "example.edu")]
    [InlineData("a@B.Org", "b.org")]
    [InlineData("x@y@z.com", "z.com")]
    [InlineData("bob@", "(unknown)")]
    [InlineData("/o=ExchangeLabs/ou=x/cn=bob", "(unknown)")]
    [InlineData("(unknown)", "(unknown)")]
    [InlineData(null, "(unknown)")]
    public void DomainOf_Cases(string? input, string expected) => Assert.Equal(expected, EmailStats.DomainOf(input));

    [Fact]
    public void SplitRecipients_DedupesAndSkipsInvalid()
    {
        const string to = "Example, Alice <alice@example.edu>; bob@x.org; not-an-address; Bob B <BOB@x.org>; ;";
        const string cc = "carol@example.edu; \"Alice A\" <ALICE@example.edu>";
        var all = EmailStats.SplitRecipients(to, cc, true);
        Assert.Equal(
            [new Recipient("Example, Alice", "alice@example.edu"), new Recipient("", "bob@x.org"), new Recipient("", "carol@example.edu")],
            all);
        var noCc = EmailStats.SplitRecipients(to, cc, false);
        Assert.Equal(
            [new Recipient("Example, Alice", "alice@example.edu"), new Recipient("", "bob@x.org")],
            noCc);
        Assert.Empty(EmailStats.SplitRecipients(null, null, true));
        Assert.Empty(EmailStats.SplitRecipients("junk; more junk", null, true));
    }

    [Fact]
    public void TimeKey_UsesZone()
    {
        var t = U(2026, 11, 16, 7, 30).ToUnixTimeSeconds();
        Assert.Equal("2026-11", EmailStats.TimeKey(StatsGroupBy.Month, t, UtcMinus8));
        Assert.Equal("2026-W46", EmailStats.TimeKey(StatsGroupBy.Week, t, UtcMinus8));
        Assert.Equal("Sun", EmailStats.TimeKey(StatsGroupBy.Weekday, t, UtcMinus8));
        Assert.Equal("23", EmailStats.TimeKey(StatsGroupBy.Hour, t, UtcMinus8));

        var d = U(2026, 12, 1, 3).ToUnixTimeSeconds();
        Assert.Equal("2026-11", EmailStats.TimeKey(StatsGroupBy.Month, d, UtcMinus8));
        Assert.Equal("2026-12", EmailStats.TimeKey(StatsGroupBy.Month, d, UtcPlus2));

        Assert.Equal("2026-W53", EmailStats.TimeKey(StatsGroupBy.Week, U(2027, 1, 1, 12).ToUnixTimeSeconds(), Utc));
        Assert.Equal("2025-W01", EmailStats.TimeKey(StatsGroupBy.Week, U(2024, 12, 30, 12).ToUnixTimeSeconds(), Utc));
        Assert.Equal("2026-W01", EmailStats.TimeKey(StatsGroupBy.Week, U(2026, 1, 1, 12).ToUnixTimeSeconds(), Utc));
        Assert.Equal("05", EmailStats.TimeKey(StatsGroupBy.Hour, U(2026, 1, 1, 5).ToUnixTimeSeconds(), Utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailStats.TimeKey(StatsGroupBy.Sender, 0, Utc));
        Assert.Throws<ArgumentNullException>(() => EmailStats.TimeKey(StatsGroupBy.Month, 0, null!));
    }

    [Fact]
    public void ParseGroupBy_AndOrder_AndWireName()
    {
        string[] wire = ["sender", "sender_domain", "recipient", "recipient_domain", "folder", "month", "week", "weekday", "hour"];
        for (var i = 0; i < wire.Length; i++)
        {
            Assert.Equal((StatsGroupBy)i, EmailStats.ParseGroupBy(wire[i]));
            Assert.Equal(wire[i], EmailStats.WireName((StatsGroupBy)i));
        }
        Assert.Equal(StatsGroupBy.SenderDomain, EmailStats.ParseGroupBy(" Sender_Domain "));
        foreach (var bad in new string?[] { null, "", "subject", "senders" })
            Assert.Equal(EmailStats.GroupByError, Assert.Throws<McpException>(() => EmailStats.ParseGroupBy(bad)).Message);

        Assert.Equal(StatsOrder.Count, EmailStats.ParseOrder(null));
        Assert.Equal(StatsOrder.Count, EmailStats.ParseOrder(""));
        Assert.Equal(StatsOrder.Key, EmailStats.ParseOrder("KEY"));
        Assert.Equal("order must be count or key.", Assert.Throws<McpException>(() => EmailStats.ParseOrder("date")).Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailStats.WireName((StatsGroupBy)99));
    }

    // ---- T2 ----

    [Fact]
    public void Count_Sender()
    {
        Seed(_ix);
        var c = Count(StatsGroupBy.Sender);
        Assert.Equal(5, c.Total);
        Assert.Equal(["(unknown)", "alice@example.edu", "bob@x.org", "me@x.org"], c.Groups.Select(g => g.Key).ToArray());
        var g = By(c);
        Assert.Equal(1, g["(unknown)"].Count);
        Assert.Equal("", g["(unknown)"].Name);
        Assert.Equal(2, g["alice@example.edu"].Count);
        Assert.Equal("Example, Alice", g["alice@example.edu"].Name);
        Assert.Equal(U(2026, 3, 2, 10), g["alice@example.edu"].First);
        Assert.Equal(U(2026, 5, 4, 9), g["alice@example.edu"].Last);
        Assert.Equal(1, g["bob@x.org"].Count);
        Assert.Equal(1, g["me@x.org"].Count);
    }

    [Fact]
    public void Count_SenderDomain()
    {
        Seed(_ix);
        var c = Count(StatsGroupBy.SenderDomain);
        Assert.Equal(5, c.Total);
        var g = By(c);
        Assert.Equal(1, g["(unknown)"].Count);
        Assert.Equal(2, g["example.edu"].Count);
        Assert.Equal(2, g["x.org"].Count);
        Assert.All(c.Groups, x => Assert.Null(x.Name));
    }

    [Fact]
    public void Count_Folder()
    {
        Seed(_ix);
        var g = By(Count(StatsGroupBy.Folder));
        Assert.Equal(3, g["Inbox"].Count);
        Assert.Equal(1, g["Inbox\\Projects"].Count);
        Assert.Equal(1, g["Sent Items"].Count);
        Assert.All(g.Values, x => Assert.Null(x.Name));
    }

    [Fact]
    public void Count_Recipient()
    {
        Seed(_ix);
        var c = Count(StatsGroupBy.Recipient);
        Assert.Equal(5, c.Total);
        var g = By(c);
        Assert.Equal(2, g["me@x.org"].Count);
        Assert.Equal("Me", g["me@x.org"].Name);
        Assert.Equal(2, g["bob@x.org"].Count);
        Assert.Equal("", g["bob@x.org"].Name);
        Assert.Equal(1, g["carol@other.net"].Count);
        Assert.Equal(1, g["alice@example.edu"].Count);
        Assert.Equal("Alice", g["alice@example.edu"].Name);
        Assert.Equal(1, g["dave@other.net"].Count);

        var nc = Count(StatsGroupBy.Recipient, cc: false);
        Assert.Equal(5, nc.Total);
        var n = By(nc);
        Assert.Equal(1, n["bob@x.org"].Count);
        Assert.Equal("Bob", n["bob@x.org"].Name);
        Assert.False(n.ContainsKey("carol@other.net"));
    }

    [Fact]
    public void Count_RecipientDomain()
    {
        Seed(_ix);
        var g = By(Count(StatsGroupBy.RecipientDomain));
        Assert.Equal(3, g["x.org"].Count);
        Assert.Equal(2, g["other.net"].Count);
        Assert.Equal(1, g["example.edu"].Count);
        Assert.All(g.Values, x => Assert.Null(x.Name));
    }

    [Fact]
    public void Count_TimeGroupings()
    {
        Seed(_ix);
        var month = By(Count(StatsGroupBy.Month));
        Assert.Equal([1L, 2L, 2L], new[] { month["2026-03"].Count, month["2026-05"].Count, month["2026-06"].Count });
        var wd = Count(StatsGroupBy.Weekday);
        Assert.Equal(5, By(wd)["Mon"].Count);
        Assert.Single(wd.Groups);
        var hour = By(Count(StatsGroupBy.Hour));
        Assert.Equal([2L, 1L, 2L], new[] { hour["09"].Count, hour["10"].Count, hour["12"].Count });
        var week = By(Count(StatsGroupBy.Week));
        Assert.Equal([1L, 2L, 2L], new[] { week["2026-W10"].Count, week["2026-W19"].Count, week["2026-W23"].Count });
        Assert.Equal(U(2026, 5, 4, 9), month["2026-05"].First);
    }

    [Fact]
    public void Count_SameTimeTie_NewestRowIdWinsName()
    {
        _ix.UpsertBatch([Build("z1", U(2026, 1, 1, 1), senderName: "First", senderEmail: "z@x.org")]);
        _ix.UpsertBatch([Build("z2", U(2026, 1, 1, 1), senderName: "Second", senderEmail: "z@x.org")]);
        var g = By(Count(StatsGroupBy.Sender));
        Assert.Equal("Second", g["z@x.org"].Name);
        Assert.Equal(2, g["z@x.org"].Count);
    }

    [Fact]
    public void Count_NonAsciiCaseMerges()
    {
        _ix.UpsertBatch([Build("n1", U(2026, 1, 1, 1), senderEmail: "ÄNNE@x.de")]);
        _ix.UpsertBatch([Build("n2", U(2026, 1, 2, 1), senderEmail: "änne@x.de")]);
        var c = Count(StatsGroupBy.Sender);
        var g = Assert.Single(c.Groups);
        Assert.Equal("änne@x.de", g.Key);
        Assert.Equal(2, g.Count);
    }

    [Fact]
    public void Count_GroupsSortedOrdinal()
    {
        Seed(_ix);
        foreach (var gb in Enum.GetValues<StatsGroupBy>())
        {
            var keys = Count(gb).Groups.Select(g => g.Key).ToArray();
            var sorted = keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(sorted, keys);
        }
    }

    [Fact]
    public void Count_EmptyIndex()
    {
        foreach (var gb in Enum.GetValues<StatsGroupBy>())
        {
            var c = Count(gb);
            Assert.Equal(0, c.Total);
            Assert.Empty(c.Groups);
        }
    }

    [Fact]
    public void Count_CancelledThrows()
    {
        Seed(_ix);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            EmailStats.Count(_ix, new SearchFilter(), StatsGroupBy.Sender, true, Utc, cts.Token));
    }

    [Fact]
    public void Count_ArgumentChecks()
    {
        Assert.Throws<ArgumentNullException>(() => EmailStats.Count(null!, new SearchFilter(), StatsGroupBy.Sender, true, Utc, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => EmailStats.Count(_ix, null!, StatsGroupBy.Sender, true, Utc, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => EmailStats.Count(_ix, new SearchFilter(), StatsGroupBy.Sender, true, null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmailStats.Count(_ix, new SearchFilter(), (StatsGroupBy)99, true, Utc, TestContext.Current.CancellationToken));
    }

    // ---- T3 ----

    public static TheoryData<string> FilterCases() => new() { "folder", "dates", "query", "recipient", "unread", "paging", "like" };

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void Filters_Apply(string which)
    {
        Seed(_ix, m5Unread: true);
        var (filter, expected) = which switch
        {
            "folder" => (new SearchFilter { Folder = "inbox" }, 3L),
            "dates" => (new SearchFilter { DateFrom = U(2026, 5, 1), DateTo = U(2026, 5, 31) }, 2L),
            "query" => (new SearchFilter { Query = "alice" }, 3L),
            "recipient" => (new SearchFilter { Recipient = "carol@other.net; dave@other.net" }, 2L),
            "unread" => (new SearchFilter { Unread = true }, 1L),
            "paging" => (new SearchFilter { Limit = 1, Offset = 3 }, 5L),
            _ => (new SearchFilter { Sender = "100%" }, 0L),
        };
        Assert.Equal(expected, Count(StatsGroupBy.Sender, filter).Total);
        Assert.Equal(expected, Count(StatsGroupBy.Recipient, filter).Total);
    }

    // ---- T4 ----

    private static StatsGroup G(string key, long n, DateTimeOffset first, DateTimeOffset last, string? name = null) =>
        new(key, name, n, first, last);

    [Fact]
    public void Merge_AddsAndPicksName()
    {
        var own = new StatsCounts(3, [G("alice", 2, U(2026, 3, 2), U(2026, 5, 4), "A1")]);
        var shared = new StatsCounts(2, [G("alice", 1, U(2026, 1, 10), U(2026, 5, 4), "A2"), G("bob", 1, U(2026, 2, 1), U(2026, 2, 1))]);
        var m = Merge(own, shared);
        Assert.Equal(5, m.Total);
        var a = By(m)["alice"];
        Assert.Equal(3, a.Count);
        Assert.Equal(U(2026, 1, 10), a.First);
        Assert.Equal(U(2026, 5, 4), a.Last);
        Assert.Equal("A1", a.Name);
        Assert.Equal(1, By(m)["bob"].Count);

        var shared2 = new StatsCounts(1, [G("alice", 1, U(2026, 1, 10), U(2026, 6, 1), "A2")]);
        Assert.Equal("A2", By(Merge(own, shared2))["alice"].Name);
        Assert.Throws<ArgumentNullException>(() => EmailStats.Merge(null!));
    }

    private static StatsCounts Merge(params StatsCounts[] c) => EmailStats.Merge(c);

    [Fact]
    public void Sort_Orders()
    {
        var t = U(2026, 1, 1);
        Assert.Equal(["c", "a", "b"],
            EmailStats.Sort([G("b", 2, t, t), G("a", 2, t, t), G("c", 5, t, t)], StatsGroupBy.Sender, StatsOrder.Count).Select(g => g.Key));
        Assert.Equal(["a", "b", "c"],
            EmailStats.Sort([G("b", 2, t, t), G("c", 5, t, t), G("a", 2, t, t)], StatsGroupBy.Sender, StatsOrder.Key).Select(g => g.Key));
        Assert.Equal(["Mon", "Wed", "Sun"],
            EmailStats.Sort([G("Sun", 1, t, t), G("Mon", 1, t, t), G("Wed", 1, t, t)], StatsGroupBy.Weekday, StatsOrder.Key).Select(g => g.Key));
        Assert.Equal(["05", "10", "23"],
            EmailStats.Sort([G("23", 1, t, t), G("05", 1, t, t), G("10", 1, t, t)], StatsGroupBy.Hour, StatsOrder.Key).Select(g => g.Key));
    }

    [Fact]
    public void Shape_TopAndTruncation()
    {
        var t = U(2026, 1, 1);
        var three = new StatsCounts(6, [G("a", 3, t, t), G("b", 2, t, t), G("c", 1, t, t)]);
        var s2 = EmailStats.Shape(three, StatsGroupBy.Sender, StatsOrder.Count, 2, "", Utc);
        Assert.Equal(2, s2.Rows.Count);
        Assert.Equal(3, s2.Distinct);
        Assert.True(s2.Truncated);
        Assert.Equal(6, s2.Total);
        Assert.Equal("sender", s2.GroupBy);
        Assert.False(EmailStats.Shape(three, StatsGroupBy.Sender, StatsOrder.Count, 3, "", Utc).Truncated);
        Assert.Single(EmailStats.Shape(three, StatsGroupBy.Sender, StatsOrder.Count, 0, "", Utc).Rows);

        var many = new StatsCounts(250, Enumerable.Range(0, 250).Select(i => G("k" + i.ToString("D3"), 1, t, t)).ToList());
        var big = EmailStats.Shape(many, StatsGroupBy.Sender, StatsOrder.Count, 500, "all", Utc);
        Assert.Equal(200, big.Rows.Count);
        Assert.True(big.Truncated);
        Assert.Equal("all", big.Mailbox);

        var none = EmailStats.Shape(new StatsCounts(0, []), StatsGroupBy.Month, StatsOrder.Count, 10, "", Utc);
        Assert.Equal("""{"group_by":"month","mailbox":"","total":0,"distinct":0,"truncated":false,"rows":[]}""",
            JsonSerializer.Serialize(none));
    }

    [Fact]
    public void Shape_FormatsTimesInZone()
    {
        var t = U(2026, 11, 14, 0, 40);
        var dto = EmailStats.Shape(new StatsCounts(1, [G("k", 1, t, t)]), StatsGroupBy.Folder, StatsOrder.Count, 10, "", UtcMinus8);
        Assert.Equal("2026-11-13T16:40:00-08:00", dto.Rows[0].First);
        Assert.Equal("2026-11-13T16:40:00-08:00", dto.Rows[0].Last);
    }

    [Fact]
    public void Json_Shape()
    {
        Seed(_ix);
        var sender = EmailStats.Shape(Count(StatsGroupBy.Sender), StatsGroupBy.Sender, StatsOrder.Count, 10, "", Utc);
        var folder = EmailStats.Shape(Count(StatsGroupBy.Folder), StatsGroupBy.Folder, StatsOrder.Count, 10, "", Utc);
        var month = EmailStats.Shape(Count(StatsGroupBy.Month), StatsGroupBy.Month, StatsOrder.Count, 10, "", Utc);

        using var sd = JsonDocument.Parse(JsonSerializer.Serialize(sender));
        Assert.Equal(["group_by", "mailbox", "total", "distinct", "truncated", "rows"],
            sd.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["key", "name", "count", "first", "last"],
            sd.RootElement.GetProperty("rows")[0].EnumerateObject().Select(p => p.Name).ToArray());
        foreach (var dto in new[] { folder, month })
        {
            using var d = JsonDocument.Parse(JsonSerializer.Serialize(dto));
            Assert.Equal(["key", "count", "first", "last"],
                d.RootElement.GetProperty("rows")[0].EnumerateObject().Select(p => p.Name).ToArray());
        }
    }

    // ---- T5 ----

    private static EmailStatsDto RunStats(MailboxIndexes mi, TimeZoneInfo zone, SearchArgs a, string? groupBy,
        int top = EmailStats.DefaultTop, string? order = null, bool includeCc = true) =>
        EmailStats.Run(mi, zone, a, groupBy, top, order, includeCc, TestContext.Current.CancellationToken);

    private MailboxIndexes NewIndexes()
    {
        var mi = new MailboxIndexes(Path.Combine(_dir, "mi"), ["shared@x.org"]);
        Seed(mi.For(""));
        mi.For("shared@x.org").UpsertBatch([
            Build("s1", U(2026, 4, 1, 8), "Inbox", "", "Alice S", "alice@example.edu", "shared@x.org"),
            Build("s2", U(2026, 4, 2, 8), "Inbox", "", "Zed", "zed@y.org", "shared@x.org"),
        ]);
        return mi;
    }

    [Fact]
    public void Run_OwnSharedAll()
    {
        using var mi = NewIndexes();
        var own = RunStats(mi, Utc, new SearchArgs(), "sender");
        Assert.Equal("", own.Mailbox);
        Assert.Equal("sender", own.GroupBy);
        Assert.Equal(5, own.Total);
        Assert.Equal(4, own.Distinct);

        var shared = RunStats(mi, Utc, new SearchArgs { Mailbox = "SHARED@x.org " }, "sender");
        Assert.Equal("shared@x.org", shared.Mailbox);
        Assert.Equal(2, shared.Total);

        var all = RunStats(mi, Utc, new SearchArgs { Mailbox = "all" }, "sender");
        Assert.Equal("all", all.Mailbox);
        Assert.Equal(7, all.Total);
        Assert.Equal(3, all.Rows.Single(r => r.Key == "alice@example.edu").Count);
    }

    [Fact]
    public void Run_Errors()
    {
        using var mi = NewIndexes();
        var ex = Assert.Throws<McpException>(() => RunStats(mi, Utc, new SearchArgs { Mailbox = "nobody@x.org" }, "sender"));
        Assert.StartsWith("Mailbox \"nobody@x.org\" is not configured or not enabled.", ex.Message);
        Assert.Equal(EmailStats.GroupByError,
            Assert.Throws<McpException>(() => RunStats(mi, Utc, new SearchArgs { DateFrom = "May" }, "subject")).Message);
        Assert.Equal(EmailStats.OrderError,
            Assert.Throws<McpException>(() => RunStats(mi, Utc, new SearchArgs(), "sender", order: "date")).Message);
        Assert.StartsWith("date_from \"May\" is not a valid date.",
            Assert.Throws<McpException>(() => RunStats(mi, Utc, new SearchArgs { DateFrom = "May" }, "sender")).Message);
    }

    [Fact]
    public void Run_IncludeCc_AndKeyOrder()
    {
        using var mi = NewIndexes();
        var args = new SearchArgs { Recipient = "bob@x.org" };
        var withCc = RunStats(mi, Utc, args, "recipient", includeCc: true);
        var noCc = RunStats(mi, Utc, args, "recipient", includeCc: false);
        Assert.Equal(2, withCc.Rows.Single(r => r.Key == "bob@x.org").Count);
        Assert.Equal(1, noCc.Rows.Single(r => r.Key == "bob@x.org").Count);

        var months = RunStats(mi, Utc, new SearchArgs(), "month", order: "key");
        Assert.Equal(["2026-03", "2026-05", "2026-06"], months.Rows.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void ToolDescription_NamesEveryGroupBy()
    {
        foreach (var g in Enum.GetValues<StatsGroupBy>())
        {
            Assert.Contains(EmailStats.WireName(g), EmailStats.ToolDescription);
            Assert.Contains(EmailStats.WireName(g), EmailStats.GroupByParameterDescription);
        }
        foreach (var s in new[] { "never mail text", "query_index", "mailbox=\"all\"", "include_cc", "recipients with no valid address are skipped", "(unknown) is a sender with no valid address" })
        {
            Assert.Contains(s, EmailStats.ToolDescription);
        }
    }

    [Fact]
    public void Dto_SerializesAsSpecified()
    {
        var o = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
        var dto = new EmailStatsDto("month", "", 3, 1, false,
            [new StatsRowDto("2026-01", null, 3, "2026-01-01T09:00:00-08:00", "2026-01-31T17:00:00-08:00")]);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, o));
        var root = doc.RootElement;
        Assert.Equal("month", root.GetProperty("group_by").GetString());
        Assert.Equal("", root.GetProperty("mailbox").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt64());
        Assert.Equal(1, root.GetProperty("distinct").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var row = root.GetProperty("rows")[0];
        Assert.False(row.TryGetProperty("name", out _));
        Assert.Equal("2026-01", row.GetProperty("key").GetString());
        Assert.Equal(3, row.GetProperty("count").GetInt64());
        Assert.Equal("2026-01-01T09:00:00-08:00", row.GetProperty("first").GetString());
        Assert.Equal("2026-01-31T17:00:00-08:00", row.GetProperty("last").GetString());

        var named = dto with { Rows = [new StatsRowDto("a@x.org", "", 1, "f", "l")] };
        Assert.Contains("\"name\":\"\"", JsonSerializer.Serialize(named, o));
    }
}
