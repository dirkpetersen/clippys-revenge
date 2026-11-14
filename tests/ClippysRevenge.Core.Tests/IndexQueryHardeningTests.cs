using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using SQLitePCL;
using Xunit;

namespace ClippysRevenge.Core.Tests;

[CollectionDefinition("SqliteHeap", DisableParallelization = true)]
public class SqliteHeapCollection;

/// <summary>
/// Memory bombs: they briefly drive SQLite to the 1 GiB process-wide heap cap, which would fail other SQLite
/// tests running at the same time, so this collection runs alone.
/// </summary>
[Collection("SqliteHeap")]
public class IndexQueryBombTests(Xunit.ITestOutputHelper output) : IndexQueryFixture
{
    private static string Nested(int depth, bool notMaterialized)
    {
        var m = notMaterialized ? " NOT MATERIALIZED" : "";
        var sb = new StringBuilder($"WITH a0 AS{m} (SELECT 1 AS x)");
        for (var i = 1; i <= depth; i++)
            sb.Append($", a{i} AS{m} (SELECT x FROM a{i - 1} UNION ALL SELECT x FROM a{i - 1})");
        sb.Append($" SELECT 1 FROM a{depth} LIMIT 1");
        return sb.ToString();
    }

    private void Healthy() => Assert.Equal(3, Q("SELECT subject FROM mail").RowCount);

    private (McpException? Error, TimeSpan Elapsed) Time(string sql)
    {
        var sw = Stopwatch.StartNew();
        McpException? error = null;
        try { IndexQuery.Run(Indexes, sql, "", 5, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        catch (McpException e) { error = e; }
        var elapsed = sw.Elapsed;
        output.WriteLine($"{sql.Length} chars: {(error is null ? "ok" : error.Message)} in {elapsed.TotalMilliseconds:F0} ms; "
            + $"SQLite heap high-water {raw.sqlite3_memory_highwater(0) / 1048576} MB");
        return (error, elapsed);
    }

    [Fact]
    public void Nested_Cte_Expansion_NotMaterialized_Is_Stopped()
    {
        Assert.True(Nested(18, true).Length < 2_000);
        var (error, elapsed) = Time(Nested(18, true));
        Assert.Equal(IndexQueryTexts.OutOfMemory, error?.Message);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), "took " + elapsed);
        Healthy();
    }

    [Fact]
    public void Nested_Cte_Expansion_Materialized_Is_Fast_Or_Stopped()
    {
        var (error, elapsed) = Time(Nested(18, false));
        if (error is not null) Assert.Equal(IndexQueryTexts.OutOfMemory, error.Message);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), "took " + elapsed);
        Healthy();
    }

    [Fact]
    public void Forty_Huge_Computed_Columns_Are_Refused()
    {
        var cols = string.Join(", ", Enumerable.Range(0, 40).Select(i => $"printf('%.*c', 49000000, '1') AS c{i}"));
        var (error, elapsed) = Time("SELECT " + cols);
        Assert.Equal(IndexQueryTexts.TooBig, error?.Message);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), "took " + elapsed);
        Healthy();
    }

    [Fact]
    public void Many_Medium_Columns_Hit_The_Heap_Cap()
    {
        // 100 columns of 15 MB each (under the 16 MB value limit) would be 1.5 GB at once.
        var cols = string.Join(", ", Enumerable.Range(0, 100).Select(i => $"printf('%.*c', 15000000, '1') AS c{i}"));
        var (error, elapsed) = Time("SELECT " + cols);
        Assert.Equal(IndexQueryTexts.OutOfMemory, error?.Message);
        Assert.True(elapsed < TimeSpan.FromSeconds(8), "took " + elapsed);
        Healthy();
    }

    [Fact]
    public void Expensive_Operations_Are_Interrupted_By_The_Deadline_Timer()
    {
        // Each step builds 15 MB temp keys; the progress handler (every 1000 ops) alone would overrun by far.
        const string sql = "WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM r LIMIT 200) "
            + "SELECT count(*) FROM r WHERE printf('%.*c',15000000,'a')||x IN (SELECT printf('%.*c',15000000,'b')||x FROM r)";
        var deadline = TimeSpan.FromSeconds(1);
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<McpException>(() =>
            IndexQuery.Run(Indexes, sql, "", 5, deadline, TestContext.Current.CancellationToken));
        output.WriteLine("timeout after " + sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
        Assert.Equal(IndexQueryTexts.Timeout, e.Message);
        Assert.True(sw.Elapsed < deadline * 2, "took " + sw.Elapsed);
        Healthy();
    }

    [Fact]
    public void Heap_Limit_Is_Set_Once_And_Idempotent()
    {
        SqliteHeapLimit.Ensure();
        SqliteHeapLimit.Ensure();
        Assert.Equal(SqliteHeapLimit.Bytes, raw.sqlite3_hard_heap_limit64(-1));
    }
}

public class IndexQueryHardeningTests : IndexQueryFixture
{
    [Fact]
    public void Prepare_Interrupt_Maps_Like_Step()
    {
        var e = Assert.Throws<McpException>(() => IndexQuery.ThrowIfInterrupted(raw.SQLITE_INTERRUPT, true, CancellationToken.None));
        Assert.Equal(IndexQueryTexts.Timeout, e.Message);
        Assert.Throws<McpException>(() => IndexQuery.ThrowIfInterrupted(raw.SQLITE_ERROR, true, CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => IndexQuery.ThrowIfInterrupted(raw.SQLITE_INTERRUPT, true, cts.Token));
        IndexQuery.ThrowIfInterrupted(raw.SQLITE_ERROR, false, CancellationToken.None);   // no interrupt: no throw
    }

    [Fact]
    public void Zero_Deadline_Fails_With_Timeout_Before_Any_Work()
    {
        var e = Assert.Throws<McpException>(() =>
            IndexQuery.Run(Indexes, "SELECT 1", "all", 5, TimeSpan.Zero, TestContext.Current.CancellationToken));
        Assert.Equal(IndexQueryTexts.Timeout, e.Message);
    }

    [Fact]
    public void Value_Limit_Is_16_MB()
    {
        Assert.Equal(16_000_000, IndexQuery.MaxValueBytes);
        Assert.Equal(IndexQueryTexts.TooBig, Fail("SELECT printf('%.*c', 17000000, 'x')").Message);
        Assert.Equal(IndexQueryTexts.TooBig, Fail("SELECT zeroblob(17000000)").Message);
        Assert.Contains("16 MB", IndexQueryTexts.TooBig);
        Assert.Contains("too much memory", IndexQueryTexts.OutOfMemory);
    }

    [Fact]
    public void Huge_Text_Cell_Is_Cut_At_2000_Chars()
    {
        var r = Q("SELECT printf('%.*c', 5000000, 'a') AS big, 'short' AS s");
        var big = Str(r.Rows[0][0]);
        Assert.Equal(IndexQuery.MaxCellChars + 1, big.Length);
        Assert.Equal(new string('a', IndexQuery.MaxCellChars) + "\u2026", big);
        Assert.Equal("short", r.Rows[0][1]);
        Assert.Equal(1, r.TruncatedCells);
    }

    [Theory]
    [InlineData("\u00e9", 1)]          // 2 bytes
    [InlineData("\u20ac", 2)]          // 3 bytes
    [InlineData("\U0001F600", 3)]      // 4 bytes, a surrogate pair
    [InlineData("a\u20ac", 4)]
    [InlineData("\U0001F600a", 5)]
    [InlineData("\u00e9\U0001F600\u20ac", 6)]
    public void Big_Multibyte_Cells_Cut_On_Character_Boundaries(string unit, int _)
    {
        // Strings of several lengths around the 2000-char cap and the internal byte window.
        foreach (var repeat in new[] { 700, 1000, 1001, 2000, 2001, 2500, 5000, 20000 })
        {
            var text = string.Concat(Enumerable.Repeat(unit, repeat));
            var r = Q($"SELECT replace(hex(zeroblob({repeat})), '00', '{unit.Replace("'", "''")}')");
            var cell = Str(r.Rows[0][0]);
            Assert.DoesNotContain('\uFFFD', cell);
            if (text.Length <= IndexQuery.MaxCellChars)
            {
                Assert.Equal(text, cell);
                continue;
            }
            Assert.EndsWith("\u2026", cell);
            var body = cell[..^1];
            Assert.True(body.Length is IndexQuery.MaxCellChars or IndexQuery.MaxCellChars - 1, "len " + body.Length);
            Assert.StartsWith(body, text, StringComparison.Ordinal);
            Assert.False(char.IsHighSurrogate(body[^1]));
        }
    }

    [Fact]
    public void Text_Cell_Of_Exactly_2000_Chars_Is_Not_Cut()
    {
        var r = Q("SELECT printf('%.*c', 2000, 'b'), printf('%.*c', 2001, 'b')");
        Assert.Equal(new string('b', 2000), r.Rows[0][0]);
        Assert.Equal(new string('b', 2000) + "\u2026", r.Rows[0][1]);
        Assert.Equal(1, r.TruncatedCells);
    }

    [Fact]
    public void Empty_Text_Cell_Is_Empty()
    {
        var r = Q("SELECT '' AS e");
        Assert.Equal("", r.Rows[0][0]);
    }

    private void AddUnknownTable()
    {
        var ix = Indexes.For("");
        using var conn = ix.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS secret_extra(a); INSERT INTO secret_extra VALUES ('s3cret');";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void NoColumnRead_Of_Unknown_Real_Table_Is_Denied_And_Cte_Still_Works()
    {
        AddUnknownTable();
        Assert.Equal(IndexQueryTexts.Denied("table secret_extra"), Fail("SELECT count(*) FROM secret_extra").Message);
        Assert.Equal(IndexQueryTexts.Denied("table secret_extra"), Fail("SELECT count(*) FROM main.secret_extra").Message);
        Assert.Equal(IndexQueryTexts.Denied("table SECRET_EXTRA"), Fail("SELECT 1 FROM SECRET_EXTRA").Message);
        Assert.Equal(IndexQueryTexts.Denied("table secret_extra"), Fail("SELECT a FROM secret_extra").Message);
        Assert.Equal(3L, Assert.Single(Q("WITH r AS (SELECT subject FROM mail) SELECT count(*) FROM r").Rows).Single());
        Assert.Equal(1L, Assert.Single(Q("SELECT count(*) FROM (SELECT 1 AS x) AS t").Rows).Single());
        Assert.Equal(3L, Assert.Single(Q("SELECT count(*) FROM mail").Rows).Single());
    }

    [Fact]
    public void Authorizer_Uses_The_Live_Table_List()
    {
        var real = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "emails", "attachments", "secret_extra" };
        Assert.False(IndexQueryAuthorizer.Decide(raw.SQLITE_READ, "secret_extra", "", "main", null, real).Allow);
        Assert.True(IndexQueryAuthorizer.Decide(raw.SQLITE_READ, "r", "", null, null, real).Allow);
        Assert.True(IndexQueryAuthorizer.Decide(raw.SQLITE_READ, "emails", "", "main", null, real).Allow);
        Assert.False(IndexQueryAuthorizer.Decide(raw.SQLITE_READ, "emails", "", null, null, real).Allow);
        Assert.False(IndexQueryAuthorizer.Decide(raw.SQLITE_READ, "dbstat", "", null, null, real).Allow);
    }

    [Fact]
    public void Busy_Wait_Ends_With_The_Deadline()
    {
        // A writer holds the lock of the own index; the query must give up with the deadline, not after a fixed 5 s.
        var path = Indexes.For("").DbPath;
        Indexes.For("").Close();
        SqliteConnection.ClearAllPools();
        using var w = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        w.Open();
        using (var j = w.CreateCommand()) { j.CommandText = "PRAGMA journal_mode=DELETE;"; j.ExecuteNonQuery(); }
        using (var b = w.CreateCommand()) { b.CommandText = "BEGIN EXCLUSIVE;"; b.ExecuteNonQuery(); }
        var sw = Stopwatch.StartNew();
        var e = Assert.Throws<McpException>(() =>
            IndexQuery.Run(Indexes, "SELECT count(*) FROM mail", "", 5, TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), "took " + sw.Elapsed);
        Assert.StartsWith("The index for ", e.Message);
        using (var b = w.CreateCommand()) { b.CommandText = "ROLLBACK;"; b.ExecuteNonQuery(); }
    }
}