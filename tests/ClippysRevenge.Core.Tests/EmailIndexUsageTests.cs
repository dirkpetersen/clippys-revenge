using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexUsageTests : IDisposable
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-11-13T12:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-usage-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private string DbFile => Path.Combine(_dir, "index.db");

    public EmailIndexUsageTests() => _ix = new EmailIndex(DbFile);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    [Fact]
    public void Sums_calls_unknown_and_tokens()
    {
        _ix.AddAiCall(T, ModelCallKind.Draft, "cli", true, new TokenUsage(10, 5, null, null));
        _ix.AddAiCall(T.AddSeconds(1), ModelCallKind.Draft, "cli", false, new TokenUsage(null, null, null, null));
        _ix.AddAiCall(T.AddSeconds(2), ModelCallKind.Revisit, "api", true, new TokenUsage(100, 20, 300, 40));

        var t = _ix.AiUsageSince(T);

        Assert.Equal(new AiUsageTotals(3, 1, 110, 25, 300, 40), t);
        Assert.Equal(450, t.In);
        Assert.Equal(25, t.Out);
        Assert.Equal(475, t.Total);
    }

    [Fact]
    public void Since_is_inclusive_and_excludes_older_rows()
    {
        _ix.AddAiCall(T, ModelCallKind.Draft, "cli", true, new TokenUsage(10, 5, null, null));
        _ix.AddAiCall(T.AddSeconds(1), ModelCallKind.Draft, "cli", true, new TokenUsage(1, 1, null, null));
        _ix.AddAiCall(T.AddSeconds(2), ModelCallKind.Draft, "cli", true, new TokenUsage(2, 2, null, null));

        var t = _ix.AiUsageSince(T.AddSeconds(1));

        Assert.Equal(2, t.Calls);
        Assert.Equal(3, t.Input);
    }

    [Fact]
    public void Empty_table_is_zero()
    {
        Assert.Equal(AiUsageTotals.Zero, _ix.AiUsageSince(T));
    }

    [Fact]
    public void Null_usage_is_an_unknown_call_with_null_columns()
    {
        _ix.AddAiCall(T, ModelCallKind.Style, "cli", false, null);
        using var c = Raw();
        var row = c.QuerySingle<(long? input, long? output, long? cr, long? cc)>(
            "SELECT input, output, cache_read, cache_creation FROM ai_calls");
        Assert.Equal((null, null, null, null), row);
        Assert.Equal(new AiUsageTotals(1, 1, 0, 0, 0, 0), _ix.AiUsageSince(T));
    }

    [Fact]
    public void Negative_values_are_stored_as_null()
    {
        _ix.AddAiCall(T, ModelCallKind.Draft, "cli", true, new TokenUsage(-1, 5, -2, 7));
        using var c = Raw();
        var row = c.QuerySingle<(long? input, long? output, long? cr, long? cc)>(
            "SELECT input, output, cache_read, cache_creation FROM ai_calls");
        Assert.Equal((null, 5L, null, 7L), row);
    }

    [Fact]
    public void Bad_backend_or_kind_throws()
    {
        Assert.Throws<ArgumentException>(() => _ix.AddAiCall(T, ModelCallKind.Draft, "CLI", true, null));
        Assert.Throws<ArgumentException>(() => _ix.AddAiCall(T, ModelCallKind.Draft, "", true, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.AddAiCall(T, (ModelCallKind)99, "cli", true, null));
        Assert.Equal(AiUsageTotals.Zero, _ix.AiUsageSince(T));
    }

    [Fact]
    public void Raw_row_has_text_kind_outcome_and_unix_seconds()
    {
        _ix.AddAiCall(T, ModelCallKind.Rescan, "api", false, new TokenUsage(1, 2, 3, 4));
        using var c = Raw();
        var row = c.QuerySingle<(long at, string kind, string backend, string outcome)>(
            "SELECT at, kind, backend, outcome FROM ai_calls");
        Assert.Equal((T.ToUnixTimeSeconds(), "rescan", "api", "failed"), row);
    }

    [Fact]
    public void PurgeDecisions_deletes_calls_older_than_30_days()
    {
        var now = T;
        _ix.AddAiCall(now - TimeSpan.FromDays(31), ModelCallKind.Draft, "cli", true, new TokenUsage(1, 1, null, null));
        _ix.AddAiCall(now - TimeSpan.FromDays(29), ModelCallKind.Draft, "cli", true, new TokenUsage(2, 2, null, null));

        var n = _ix.PurgeDecisions(now);

        Assert.Equal(1, n);
        var t = _ix.AiUsageSince(DateTimeOffset.UnixEpoch);
        Assert.Equal(1, t.Calls);
        Assert.Equal(2, t.Input);
    }

    [Fact]
    public void PurgeOlderThan_deletes_old_calls_too()
    {
        _ix.AddAiCall(DateTimeOffset.UtcNow - TimeSpan.FromDays(40), ModelCallKind.Draft, "cli", true, null);
        _ix.AddAiCall(DateTimeOffset.UtcNow - TimeSpan.FromDays(1), ModelCallKind.Draft, "cli", true, null);

        _ix.PurgeOlderThan(DateTimeOffset.UtcNow - TimeSpan.FromDays(365));

        Assert.Equal(1, _ix.AiUsageSince(DateTimeOffset.UnixEpoch).Calls);
    }
}
