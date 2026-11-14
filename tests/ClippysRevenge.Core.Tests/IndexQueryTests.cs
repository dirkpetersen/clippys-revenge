using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Seeded three-mailbox harness shared by the IndexQuery tests (temp dirs only).</summary>
public abstract class IndexQueryFixture : IDisposable
{
    protected const string Shared = "shared@example.com";
    protected const string OBrien = "o'brien@example.com";

    protected static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    protected static DateTimeOffset T(int n) => Base.AddHours(n);

    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "clippy-iq-" + Guid.NewGuid().ToString("N"));
    protected readonly MailboxIndexes Indexes;

    protected IndexQueryFixture()
    {
        Indexes = new MailboxIndexes(Dir, [Shared, OBrien]);
        SeedOwn(Base);
        Indexes.For(Shared).UpsertBatch([
            Build("s1", T(1), "Inbox", "Alpha", "Sam", "sam@example.com", "", "", "alpha body", false),
            Build("s2", T(2), "Inbox", "Beta", "Sam", "sam@example.com", "", "", "beta body", false),
        ]);
        Indexes.For(OBrien).UpsertBatch([
            Build("q1", T(1), "Inbox", "Gamma", "Quinn", "quinn@example.com", "", "", "gamma body", false),
        ]);
        foreach (var key in Indexes.Mailboxes)
        {
            var ix = Indexes.For(key);
            ix.SetMeta("seeded", "yes");
            ix.UpsertDecision(new AiDecision("x-" + key, "imid", null, T(5), AiOutcome.NoReply));
        }
    }

    protected void SeedOwn(DateTimeOffset b)
    {
        var own = Indexes.For("");
        own.UpsertBatch([
            Build("o1", b.AddHours(1), "Inbox", "Budget 2026 review", "Alice Smith", "alice@example.com",
                "Bob <bob@contoso.com>", "", "Please review the budget sheet. budget budget", true, ("budget.xlsx", false)),
            Build("o2", b.AddHours(2), "Sent Items", "Lunch", "Me", "me@contoso.com",
                "Carol <carol@fabrikam.com>", "Dan <dan@contoso.com>", "Caf\u00e9 at noon?", false, ("logo.png", true)),
            Build("o3", b.AddHours(3), "Inbox", "Project plan", "Bob", "bob@contoso.com",
                "Me <me@contoso.com>", "", "The budget is in the plan", false, ("Plan.PDF", false), ("sig.png", true)),
        ]);
        own.SetThreadMeta([("o1", "<irt@x>", "Budget 2026 review", "ai")]);
    }

    protected static IndexedEmail Build(string id, DateTimeOffset t, string folder, string subject,
        string senderName, string senderEmail, string to, string cc, string body, bool unread,
        params (string name, bool inline)[] atts) => new()
    {
        EntryId = id,
        StoreId = "store-" + id,
        InternetMessageId = "<imid-" + id + "@x>",
        InReplyTo = "<irt-" + id + "@x>",
        ClippyMarker = null,
        LastModified = t,
        Folder = folder,
        Subject = subject,
        SenderName = senderName,
        SenderEmail = senderEmail,
        To = to,
        Cc = cc,
        Body = body,
        Unread = unread,
        EventTime = t,
        Attachments = atts.Select((a, i) => new AttachmentInfo(i + 1, a.name, 100, a.inline)).ToList(),
    };

    protected IndexQueryResult Q(string? sql, string? mailbox = "", int maxRows = 200, CancellationToken? ct = null) =>
        IndexQuery.Run(Indexes, sql, mailbox, maxRows, ct ?? TestContext.Current.CancellationToken);

    protected McpException Fail(string? sql, string? mailbox = "", int maxRows = 200)
    {
        try { Q(sql, mailbox, maxRows); }
        catch (McpException e) { return e; }
        throw new Xunit.Sdk.XunitException("Expected an McpException for: " + sql);
    }

    protected static string Str(object? o) => Assert.IsType<string>(o);

    public void Dispose()
    {
        Indexes.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public class IndexQueryTests : IndexQueryFixture
{
    private static readonly string[] MailColumns =
        ["id", "doc", "mailbox", "folder", "date", "unix_time", "sender_name", "sender_email", "to_addrs", "cc_addrs",
         "subject", "topic", "body", "unread", "has_attachments", "attachment_names"];

    private static object?[][] Rows(IndexQueryResult r) => r.Rows.Select(x => x.ToArray()).ToArray();

    private string[] Ids(string sql) => Q(sql).Rows.Select(r => Str(r[0])).ToArray();

    [Fact]
    public void T21_Views_And_Columns()
    {
        var r = Q("SELECT * FROM mail ORDER BY unix_time");
        Assert.Equal(MailColumns, r.Columns);
        Assert.Equal(3, r.RowCount);
        var rows = Rows(r);
        Assert.Equal(["o1", "o2", "o3"], rows.Select(x => x[0]).ToArray());
        Assert.All(rows, x => Assert.Equal("", x[2]));
        Assert.Equal([1L, 0L, 0L], rows.Select(x => x[13]).ToArray());
        Assert.Equal([1L, 0L, 1L], rows.Select(x => x[14]).ToArray());
        Assert.Equal("Budget 2026 review", rows[0][11]);
        Assert.Equal("Plan.PDF\nsig.png", rows[2][15]);
        for (var i = 0; i < 3; i++)
        {
            var t = T(i + 1);
            Assert.Equal(t.ToUnixTimeSeconds(), rows[i][5]);
            var date = Str(rows[i][4]);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", date);
            Assert.Equal(TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), date);
            Assert.NotNull(Indexes.For("").Get(Str(rows[i][0])));
        }

        var a = Q("SELECT * FROM mail_attachments ORDER BY doc, position");
        Assert.Equal(["doc", "position", "filename", "size", "is_inline"], a.Columns);
        Assert.Equal(4, a.RowCount);
        Assert.Equal([0L, 1L, 0L, 1L], a.Rows.Select(x => (long)x[4]!).ToArray());

        Assert.Equal(3, Q("SELECT doc, subject FROM mail_fts").RowCount);
        Q("SELECT * FROM mail_fts LIMIT 1");
    }

    [Fact]
    public void T22_Mailbox_Literal()
    {
        var r = Q("SELECT DISTINCT mailbox FROM mail", OBrien);
        Assert.Equal(OBrien, Assert.Single(r.Rows).Single());
        Assert.Contains("'o''brien@example.com'", IndexQuery.ViewDdl(OBrien));
    }

    [Fact]
    public void T23_Spec_Examples()
    {
        var now = DateTimeOffset.UtcNow;
        SeedOwn(now.AddDays(-30).AddHours(-5));   // o1..o3 land 29 days ago, all within the last year
        var top = Q("SELECT sender_email, count(*) AS n FROM mail WHERE folder = 'Inbox' AND unix_time >= strftime('%s','now','-1 year') GROUP BY sender_email ORDER BY n DESC LIMIT 10");
        Assert.Equal(2, top.RowCount);
        Assert.All(top.Rows, r => Assert.Equal(1L, r[1]));

        var months = Q("SELECT substr(date,1,7) AS month, count(*) AS n FROM mail GROUP BY month ORDER BY month");
        Assert.NotEmpty(months.Rows);
        Assert.All(months.Rows, r => Assert.Matches(@"^\d{4}-\d\d$", Str(r[0])));

        var pdf = Q("SELECT m.sender_email, count(*) AS n FROM mail_attachments a JOIN mail m ON m.doc = a.doc WHERE lower(a.filename) LIKE '%.pdf' GROUP BY 1 ORDER BY n DESC LIMIT 20");
        var row = Assert.Single(pdf.Rows);
        Assert.Equal("bob@contoso.com", row[0]);
        Assert.Equal(1L, row[1]);
    }

    private const string FtsJoin =
        "SELECT m.id, m.date, m.subject, snippet(mail_fts, 1, '**', '**', '\u2026', 12) AS hit FROM mail_fts JOIN mail m ON m.doc = mail_fts.doc WHERE mail_fts MATCH '{0}' ORDER BY rank LIMIT 20";

    [Fact]
    public void T24_Fts_Through_The_View()
    {
        var r = Q(string.Format(FtsJoin, "budget"));
        Assert.Equal(["o1", "o3"], r.Rows.Select(x => Str(x[0])).ToArray());
        Assert.All(r.Rows, x => Assert.Contains("**budget**", Str(x[3]), StringComparison.OrdinalIgnoreCase));

        var hl = Q("SELECT m.id, highlight(mail_fts, 0, '[', ']') AS h FROM mail_fts JOIN mail m ON m.doc = mail_fts.doc WHERE mail_fts MATCH 'budget'");
        Assert.Equal("[Budget] 2026 review", Str(hl.Rows.Single(x => Str(x[0]) == "o1")[1]));

        var bm = Q("SELECT bm25(mail_fts) AS s FROM mail_fts WHERE mail_fts MATCH 'budget'");
        Assert.Equal(2, bm.RowCount);
        Assert.All(bm.Rows, x => Assert.True(Assert.IsType<double>(x[0]) < 0));

        Assert.Equal(["o1"], Ids(string.Format(FtsJoin, "subject:budget")));
        Assert.Equal(["o1", "o3"], Q(string.Format(FtsJoin, "budg*")).Rows.Select(x => Str(x[0])).Order().ToArray());
        Assert.Equal(["o2"], Ids(string.Format(FtsJoin, "caf\u00e9")));
        Assert.Equal(["o2"], Ids(string.Format(FtsJoin, "cafe")));

        var rank = Q("SELECT rank FROM mail_fts WHERE mail_fts MATCH 'budget' ORDER BY rank");
        var ranks = rank.Rows.Select(x => Assert.IsType<double>(x[0])).ToArray();
        Assert.Equal(ranks.Order().ToArray(), ranks);
    }

    private const string Recursive =
        "WITH RECURSIVE r(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM r LIMIT 5000) SELECT n FROM r";

    [Fact]
    public void T25_MaxRows()
    {
        var r = Q(Recursive, maxRows: 10);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), r.Rows.Select(x => (long)x[0]!));
        Assert.True(r.Truncated);
        Assert.Equal("max_rows", r.TruncatedReason);
        Assert.Equal(1, Q(Recursive, maxRows: 0).RowCount);
        Assert.Equal(1000, Q(Recursive, maxRows: 5000).RowCount);
        var exact = Q("WITH RECURSIVE r(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM r LIMIT 5000) SELECT n FROM r LIMIT 7", maxRows: 7);
        Assert.Equal(7, exact.RowCount);
        Assert.False(exact.Truncated);
        Assert.Null(exact.TruncatedReason);
    }

    [Fact]
    public void T26_Cells()
    {
        var r = Q("SELECT 1, 2.5, NULL, 'x', x'010203', 1e999");
        Assert.Equal(new object?[] { 1L, 2.5, null, "x", "[blob, 3 bytes]", null }, r.Rows[0].ToArray());
        Assert.Equal(0, r.TruncatedCells);

        var s = Str(Q("SELECT printf('%.3000c', 'a')").Rows[0][0]);
        Assert.Equal(2001, s.Length);
        Assert.EndsWith("\u2026", s);
        Assert.Equal(1, Q("SELECT printf('%.3000c', 'a')").TruncatedCells);

        var sur = Str(Q("SELECT printf('%.1999c', 'a') || '\U0001F600' || 'b'").Rows[0][0]);
        Assert.Equal(new string('a', 1999) + "\u2026", sur);

        var two = Q("SELECT printf('%.3000c', 'a'), printf('%.3000c', 'b')");
        Assert.Equal(2, two.TruncatedCells);
    }

    [Fact]
    public void T27_Size_Cap()
    {
        var r = Q("WITH RECURSIVE r(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM r LIMIT 600) SELECT printf('%.1000c','a'), printf('%.1000c','b') FROM r", maxRows: 1000);
        Assert.Equal(500, r.RowCount);
        Assert.True(r.Truncated);
        Assert.Equal("size", r.TruncatedReason);
    }

    [Fact]
    public void T28_Column_Cap()
    {
        string Cols(int n) => "SELECT " + string.Join(",", Enumerable.Range(1, n));
        Assert.Equal(IndexQueryTexts.TooManyColumns, Fail(Cols(101)).Message);
        Assert.Equal(100, Q(Cols(100)).Columns.Count);
    }

    [Fact]
    public void T29_All_Mailboxes()
    {
        var r = Q("SELECT subject FROM mail ORDER BY subject", "all");
        Assert.Equal(["mailbox", "subject"], r.Columns);
        Assert.Equal(
            [("", "Budget 2026 review"), ("", "Lunch"), ("", "Project plan"), (Shared, "Alpha"), (Shared, "Beta"), (OBrien, "Gamma")],
            r.Rows.Select(x => (Str(x[0]), Str(x[1]))).ToArray());
        Assert.False(r.Truncated);

        var counts = Q("SELECT count(*) AS n FROM mail", "all");
        Assert.Equal([("", 3L), (Shared, 2L), (OBrien, 1L)], counts.Rows.Select(x => (Str(x[0]), (long)x[1]!)).ToArray());

        var four = Q("SELECT subject FROM mail", "all", 4);
        Assert.Equal(4, four.RowCount);
        Assert.True(four.Truncated);
        Assert.Equal("max_rows", four.TruncatedReason);

        var three = Q("SELECT subject FROM mail", "all", 3);
        Assert.Equal(3, three.RowCount);
        Assert.All(three.Rows, x => Assert.Equal("", x[0]));
        Assert.True(three.Truncated);

        var one = Q("SELECT subject FROM mail ORDER BY subject", "SHARED@example.com ");
        Assert.Equal(["subject"], one.Columns);
        Assert.Equal(2, one.RowCount);
    }

    [Fact]
    public void T210_Mailbox_Errors()
    {
        var e = Assert.Throws<McpException>(() => Q("SELECT 1", "nobody@x.com"));
        Assert.Equal(MailboxIndexes.Unknown("nobody@x.com", Indexes.Mailboxes).Message, e.Message);
    }

    private void Replace(string mailbox, Action<string> make)
    {
        var ix = Indexes.For(mailbox);
        ix.Close();
        SqliteConnection.ClearAllPools();
        var path = ix.DbPath;
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        make(path);
    }

    [Fact]
    public void T211_Unavailable()
    {
        var expected = $"The index for {OBrien} isn't available right now. Check index_status.";
        var path = Indexes.For(OBrien).DbPath;
        Replace(OBrien, _ => { });
        Assert.Equal(expected, Fail("SELECT 1", OBrien).Message);

        Replace(OBrien, p => File.WriteAllBytes(p, []));
        Assert.Equal(expected, Fail("SELECT 1", OBrien).Message);

        Replace(OBrien, p =>
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = p, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE x(a)";
                cmd.ExecuteNonQuery();
            }
        });
        var e = Fail("SELECT 1", OBrien);
        Assert.Equal(expected, e.Message);
        Assert.DoesNotContain(path, e.Message);

        Replace("", _ => { });
        Assert.Equal("The index for your own mailbox isn't available right now. Check index_status.", Fail("SELECT 1", "").Message);
    }

    [Fact]
    public void T212_Input_Errors()
    {
        foreach (var s in new string?[] { null, "", "   ", "-- nothing" })
            Assert.Equal(IndexQueryTexts.SqlRequired, Fail(s).Message);
        Assert.Equal(IndexQueryTexts.SqlTooLong, Fail("SELECT 1" + new string(' ', 19_993)).Message);
        Assert.Equal(1, Q("SELECT 1" + new string(' ', 19_992)).RowCount);
    }

    [Fact]
    public void T213_Sql_Errors()
    {
        var m = Fail("SELECT nope FROM mail").Message;
        Assert.StartsWith("SQL error: no such column: nope. query_index reads the views", m);
        Assert.EndsWith("(see the tool description).", m);
        Assert.DoesNotContain("..", m);
        Assert.StartsWith("SQL error: ", Fail("SELECT * FROM mail WHERE mail_fts MATCH 'x'").Message);
        Assert.StartsWith("SQL error: ", Fail("SELECT * FROM mail_fts WHERE mail_fts MATCH '\"unbalanced'").Message);
    }

    [Fact]
    public void T214_Cancellation()
    {
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        Assert.Throws<OperationCanceledException>(() => Q("SELECT 1", ct: pre.Token));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);
        var started = DateTime.UtcNow;
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Q("WITH RECURSIVE r(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM r) SELECT count(*) FROM r", ct: cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void T215_Statements_Dont_Leak()
    {
        Q("SELECT * FROM mail");
        Q(string.Format(FtsJoin, "budget"));
        Fail("DELETE FROM mail");
        var fresh = new EmailIndex(Indexes.For("").DbPath);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fresh.DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE name LIKE 'mail%'";
            using var rd = cmd.ExecuteReader();
            Assert.False(rd.Read());
        }
        Assert.Equal("2", fresh.GetMeta("schema_version"));
    }
}
