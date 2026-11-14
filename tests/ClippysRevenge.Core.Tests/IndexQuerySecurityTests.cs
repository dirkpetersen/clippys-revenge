using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class IndexQuerySecurityTests : IndexQueryFixture
{
    private static string Denied(string what) => IndexQueryTexts.Denied(what);

    private void ExpectDenied(string what, params string[] sqls)
    {
        foreach (var sql in sqls)
            Assert.True(Denied(what) == Fail(sql).Message, "wrong message for: " + sql);
    }

    private static readonly string[] WriteStatements =
    [
        "DELETE FROM mail", "delete from emails", "INSERT INTO mail(id) VALUES ('x')", "UPDATE emails SET unread = 0",
        "REPLACE INTO meta VALUES ('a','b')", "DROP VIEW mail", "DROP TABLE emails", "CREATE TABLE t(a)",
        "CREATE TEMP TABLE t(a)", "ALTER TABLE emails ADD COLUMN z", "REINDEX", "ANALYZE", "VACUUM",
        "VACUUM INTO 'x.db'",
        "WITH x AS (SELECT 1) DELETE FROM emails", "WITH x AS (SELECT 1) INSERT INTO meta VALUES ('a','b')",
    ];

    [Fact]
    public void T31_Writes()
    {
        ExpectDenied("writing", WriteStatements);
        Assert.False(File.Exists("x.db"));
        Assert.False(File.Exists(Path.Combine(Dir, "x.db")));
    }

    private static readonly (string What, string[] Sql)[] Misc =
    [
        ("ATTACH", ["ATTACH 'x.db' AS x", "ATTACH DATABASE ':memory:' AS m"]),
        ("DETACH", ["DETACH main"]),
        ("PRAGMA", ["PRAGMA table_info(emails)", "PRAGMA query_only = OFF", "  /* c */ pragma writable_schema=1"]),
        ("transactions", ["BEGIN", "COMMIT", "SAVEPOINT a"]),
        ("EXPLAIN", ["EXPLAIN SELECT 1"]),
        ("table pragma_table_info", ["SELECT * FROM pragma_table_info('emails')"]),
    ];

    [Fact]
    public void T32_Attach_Pragma_Transactions()
    {
        foreach (var (what, sqls) in Misc) ExpectDenied(what, sqls);
        Assert.False(File.Exists("x.db"));
    }

    [Fact]
    public void T33_Multiple_Statements()
    {
        foreach (var sql in new[]
        {
            "SELECT 1; SELECT 2", "SELECT 1; DROP TABLE emails", "SELECT 1;; ;SELECT 2", "SELECT 1; ATTACH 'x' AS y",
            "SELECT 1 /* a */ ; -- b\n DELETE FROM mail",
        })
            Assert.True(IndexQueryTexts.MultipleStatements == Fail(sql).Message, sql);
        foreach (var sql in new[] { "SELECT 1;", "SELECT 1; -- done", "SELECT 1 /* c */ ;;" })
        {
            var r = Q(sql);
            Assert.Equal(1L, Assert.Single(r.Rows).Single());
        }
    }

    private static string Prefix(string what) => Denied(what);

    [Fact]
    public void T34_Hidden_Data()
    {
        var prefix = Denied("table sqlite_");
        prefix = prefix[..^1];   // drop the final "."
        foreach (var sql in new[] { "SELECT * FROM sqlite_master", "SELECT * FROM sqlite_schema", "SELECT * FROM sqlite_temp_master" })
            Assert.StartsWith(prefix, Fail(sql).Message);
        foreach (var t in new[] { "meta", "sync_state", "ai_decisions" })
            Assert.Equal(Denied("table " + t), Fail("SELECT * FROM " + t).Message);
        foreach (var c in new[] { "entry_id", "store_id", "internet_message_id", "in_reply_to", "clippy_marker" })
            Assert.Equal(Denied("column emails." + c), Fail($"SELECT {c} FROM emails").Message);
        Assert.Equal(Denied("table emails"), Fail("SELECT count(*) FROM emails").Message);
        Assert.Equal(3, Q("SELECT subject FROM emails").RowCount);
        Q("SELECT * FROM emails_fts_config");
    }

    [Fact]
    public void T35_View_Name_Impersonation()
    {
        Assert.Equal(Denied("column emails.store_id"),
            Fail("WITH mail AS (SELECT store_id AS x FROM emails) SELECT * FROM mail").Message);
        Assert.Equal(Denied("table meta"),
            Fail("WITH mail AS (SELECT key, value FROM meta) SELECT * FROM mail").Message);
        Assert.Equal(Denied("table ai_decisions"),
            Fail("SELECT * FROM (SELECT outcome FROM ai_decisions) AS mail_fts").Message);
        Assert.Equal(3, Q("WITH mail AS (SELECT entry_id FROM emails) SELECT * FROM mail").RowCount);
    }

    [Fact]
    public void T36_Functions()
    {
        Assert.Equal(Denied("function load_extension"), Fail("SELECT load_extension('x')").Message);
        Assert.Equal(Denied("function sqlite_compileoption_get"), Fail("SELECT sqlite_compileoption_get(0)").Message);
        Assert.IsType<string>(Assert.Single(Q("SELECT sqlite_version()").Rows).Single());
        Fail("SELECT fts3_tokenizer('simple')");
        Fail("SELECT writefile('x','y')");
        Assert.False(File.Exists("x"));
        Assert.False(File.Exists(Path.Combine(Dir, "x")));
    }

    [Fact]
    public void T37_Parameters()
    {
        foreach (var sql in new[] { "SELECT ?", "SELECT * FROM mail WHERE id = :id", "SELECT @a", "SELECT $a", "SELECT ?1" })
            Assert.True(IndexQueryTexts.NoParameters == Fail(sql).Message, sql);
        Assert.Equal("?", Assert.Single(Q("SELECT '?' AS q").Rows).Single());
    }

    private const string Cross =
        "SELECT count(*) FROM mail a, mail b, mail c, mail d, mail e, mail f, mail g, mail h, mail i, mail j, mail k, mail l, mail m, mail n, mail o, mail p, mail q, mail r, mail s, mail t";

    private void ExpectTimeout(string sql, string mailbox)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<McpException>(() =>
            IndexQuery.Run(Indexes, sql, mailbox, 200, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        Assert.Equal(IndexQueryTexts.Timeout, e.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "took " + sw.Elapsed);
    }

    [Fact]
    public void T38_Deadline()
    {
        ExpectTimeout("WITH RECURSIVE r(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM r) SELECT count(*) FROM r", "");
        ExpectTimeout(Cross, "");
        ExpectTimeout(Cross, "all");
    }

    [Fact]
    public void T39_Memory_Bombs()
    {
        foreach (var sql in new[]
        {
            "SELECT randomblob(1000000000)", "SELECT zeroblob(2000000000)", "SELECT printf('%.*c', 1000000000, 'x')",
        })
            Assert.Equal(IndexQueryTexts.TooBig, Fail(sql).Message);
    }

    private static (string Hash, long Length) Fingerprint(string path) =>
        (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), new FileInfo(path).Length);

    private static long Count(string path, string table)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM " + table;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void T310_File_Unchanged()
    {
        var path = Indexes.For("").DbPath;
        Indexes.For("").Close();
        SqliteConnection.ClearAllPools();
        var before = Fingerprint(path);
        var counts = new[] { "emails", "attachments", "meta", "ai_decisions" }.Select(t => Count(path, t)).ToArray();

        var all = new List<string>(WriteStatements)
        {
            "SELECT * FROM mail ORDER BY unix_time", "SELECT * FROM mail_attachments ORDER BY doc, position",
            "SELECT doc, subject FROM mail_fts",
            "SELECT m.id, snippet(mail_fts, 1, '**', '**', '\u2026', 12) AS hit FROM mail_fts JOIN mail m ON m.doc = mail_fts.doc WHERE mail_fts MATCH 'budget' ORDER BY rank",
            "SELECT bm25(mail_fts) FROM mail_fts WHERE mail_fts MATCH 'budget'",
            "SELECT * FROM sqlite_master", "SELECT * FROM meta", "SELECT entry_id FROM emails", "SELECT load_extension('x')",
            "SELECT ?", "SELECT 1; SELECT 2", "SELECT randomblob(1000000000)",
            "ATTACH 'x.db' AS x", "PRAGMA writable_schema=1", "BEGIN", "SELECT * FROM pragma_table_info('emails')",
            "WITH mail AS (SELECT store_id AS x FROM emails) SELECT * FROM mail",
        };
        foreach (var sql in all)
        {
            try { IndexQuery.Run(Indexes, sql, "", 200, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); }
            catch (McpException) { }
        }
        foreach (var sql in new[] { Cross, "WITH RECURSIVE r(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM r) SELECT count(*) FROM r" })
        {
            try { IndexQuery.Run(Indexes, sql, "", 200, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken); }
            catch (McpException) { }
        }
        SqliteConnection.ClearAllPools();

        // index.db itself is byte-identical. SQLite may leave index.db-wal / index.db-shm next to a read-only
        // connection of a WAL database (even after it closed); that is expected and harmless, nothing else may appear.
        Assert.All(Directory.GetFiles(Path.GetDirectoryName(path)!, "index.db*"),
            p => Assert.Matches(@"^index\.db(-wal|-shm)?$", Path.GetFileName(p)));
        Assert.Equal(before, Fingerprint(path));
        Assert.Equal(counts, new[] { "emails", "attachments", "meta", "ai_decisions" }.Select(t => Count(path, t)).ToArray());
        var fresh = new EmailIndex(path);
        Assert.Equal("2", fresh.GetMeta("schema_version"));
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'mail%'";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void T311_No_Pool_Leak()
    {
        Q("SELECT * FROM mail");
        Fail("SELECT * FROM meta");
        var ix = Indexes.For("");
        using (var conn = ix.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA query_only";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        ix.SetMeta("x", "y");
        Assert.Equal("y", ix.GetMeta("x"));
    }
}
