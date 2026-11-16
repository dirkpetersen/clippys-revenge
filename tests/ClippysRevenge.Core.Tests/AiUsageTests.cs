using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class AiUsageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-aiu-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLog _log = new();

    public AiUsageTests() => _ix = new EmailIndex(Path.Combine(_dir, "index.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SetNow(DateTimeOffset utc) => _time.Advance(utc - _time.GetUtcNow());

    private static readonly TokenUsage U = new(10, 5, null, null);

    private void Row(DateTimeOffset at) => _ix.AddAiCall(at, ModelCallKind.Draft, "cli", true, U);

    private static DateTimeOffset Pst(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.FromHours(-8));
    private static DateTimeOffset Pdt(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void Load_sums_only_rows_since_local_midnight()
    {
        var zone = TestZones.PacificDst();
        SetNow(Pst(2026, 11, 14, 10, 0));
        Row(Pst(2026, 11, 13, 23, 59));
        Row(Pst(2026, 11, 14, 0, 1));
        var usage = new AiUsage(() => _ix, zone, _time, _log);

        usage.Load();

        Assert.Equal(1, usage.Today().Calls);
    }

    [Fact]
    public void Day_rollover_resets_the_totals()
    {
        var zone = TestZones.Utc();
        SetNow(new DateTimeOffset(2026, 11, 14, 23, 50, 0, TimeSpan.Zero));
        var usage = new AiUsage(() => _ix, zone, _time, _log);
        usage.Record(ModelCallKind.Draft, AiBackend.ClaudeCode, true, U);
        Assert.Equal(1, usage.Today().Calls);

        _time.Advance(TimeSpan.FromMinutes(20));

        Assert.Equal(AiUsageTotals.Zero, usage.Today());
        usage.Record(ModelCallKind.Draft, AiBackend.ClaudeCode, true, U);
        Assert.Equal(1, usage.Today().Calls);
    }

    [Fact]
    public void Record_after_midnight_without_a_read_still_rolls_over()
    {
        SetNow(new DateTimeOffset(2026, 11, 14, 23, 50, 0, TimeSpan.Zero));
        var usage = new AiUsage(() => _ix, TestZones.Utc(), _time, _log);
        usage.Record(ModelCallKind.Draft, AiBackend.ClaudeCode, true, U);
        _time.Advance(TimeSpan.FromMinutes(20));
        usage.Record(ModelCallKind.Draft, AiBackend.ClaudeCode, true, U);
        Assert.Equal(1, usage.Today().Calls);
    }

    [Fact]
    public void Spring_forward_day_counts_both_ends_and_not_the_day_before()
    {
        var zone = TestZones.PacificDst();
        SetNow(Pdt(2026, 3, 8, 12, 0));
        Row(Pst(2026, 3, 7, 23, 59));
        Row(Pst(2026, 3, 8, 0, 30));
        Row(Pdt(2026, 3, 8, 23, 30));
        var usage = new AiUsage(() => _ix, zone, _time, _log);
        usage.Load();
        Assert.Equal(2, usage.Today().Calls);
    }

    [Fact]
    public void Fall_back_day_counts_both_ends_and_not_the_day_before()
    {
        var zone = TestZones.PacificDst();
        SetNow(Pst(2026, 11, 1, 12, 0));
        Row(Pdt(2026, 10, 31, 23, 59));
        Row(Pdt(2026, 11, 1, 0, 30));
        Row(Pst(2026, 11, 1, 23, 30));
        var usage = new AiUsage(() => _ix, zone, _time, _log);
        usage.Load();
        Assert.Equal(2, usage.Today().Calls);
    }

    [Fact]
    public void LocalMidnightUtc_with_a_dst_gap_at_midnight_moves_one_hour_later()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 8),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Midnight", TimeSpan.FromHours(-3), "M", "M std", "M dst", [rule]);
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 8, 0, 0, 0)));

        var now = new DateTimeOffset(2026, 3, 8, 15, 0, 0, TimeSpan.Zero);
        var midnight = AiUsage.LocalMidnightUtc(now, zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 1, 0, 0, TimeSpan.FromHours(-2)), midnight);
    }

    [Fact]
    public void Load_with_a_throwing_index_is_quiet_and_zero()
    {
        SetNow(Pst(2026, 11, 14, 10, 0));
        var usage = new AiUsage(() => throw new InvalidOperationException("SECRET"), TestZones.PacificDst(), _time, _log);

        usage.Load();

        Assert.Equal(AiUsageTotals.Zero, usage.Today());
        var line = Assert.Single(_log.Lines);
        Assert.Contains("InvalidOperationException", line);
        Assert.DoesNotContain("SECRET", line);
    }

    [Fact]
    public void Constructor_rejects_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new AiUsage(null!, TestZones.Utc()));
        Assert.Throws<ArgumentNullException>(() => new AiUsage(() => _ix, null!));
    }

    [Fact]
    public void BackendName_maps_and_rejects_unknown()
    {
        Assert.Equal("cli", AiUsage.BackendName(AiBackend.ClaudeCode));
        Assert.Equal("api", AiUsage.BackendName(AiBackend.ApiKey));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiUsage.BackendName((AiBackend)9));
    }

    [Fact]
    public void Totals_Add_counts_unknown_calls_and_sums_partial_fields()
    {
        var t = AiUsageTotals.Zero
            .Add(new TokenUsage(10, 5, null, null))
            .Add(null)
            .Add(new TokenUsage(null, null, 7, null));
        Assert.Equal(new AiUsageTotals(3, 2, 10, 5, 7, 0), t);
    }

    [Fact]
    public void Totals_AddCall_uses_the_recorded_rule()
    {
        var rl = new ModelResult.RateLimited(null);
        Assert.Same(AiUsageTotals.Zero, AiUsageTotals.Zero.AddCall(rl));
        var ok = new ModelResult.Ok(JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.Zero, 1, 1, null);
        var t = AiUsageTotals.Zero.AddCall(ok);
        Assert.Equal(1, t.Calls);
        Assert.Equal(1, t.UnknownCalls);
        Assert.Throws<ArgumentNullException>(() => AiUsageTotals.Zero.AddCall(null!));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(-5L, "0")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1k")]
    [InlineData(1234L, "1.2k")]
    [InlineData(1250L, "1.3k")]
    [InlineData(12345L, "12.3k")]
    [InlineData(99949L, "99.9k")]
    [InlineData(99950L, "100k")]
    [InlineData(100000L, "100k")]
    [InlineData(210000L, "210k")]
    [InlineData(999499L, "999k")]
    [InlineData(999500L, "1M")]
    [InlineData(1000000L, "1M")]
    [InlineData(1234567L, "1.2M")]
    [InlineData(3000000L, "3M")]
    public void TokenCount_format(long n, string expected) => Assert.Equal(expected, TokenCount.Format(n));

    [Fact]
    public void Texts_Today()
    {
        Assert.Equal("Today: no AI calls", AiUsageTexts.Today(AiUsageTotals.Zero));
        Assert.Equal("Today: 2 AI calls, tokens unknown", AiUsageTexts.Today(new AiUsageTotals(2, 2, 0, 0, 0, 0)));
        Assert.Equal("Today: 1 AI call, ~1.2k tokens (in 1k / out 234)", AiUsageTexts.Today(new AiUsageTotals(1, 0, 1000, 234, 0, 0)));
        Assert.Equal("Today: 18 AI calls, ~210k tokens (in 190k / out 20k)",
            AiUsageTexts.Today(new AiUsageTotals(18, 0, 150000, 20000, 30000, 10000)));
        Assert.Equal("Today: 3 AI calls, ~12k tokens (in 10k / out 2k), 1 without token data",
            AiUsageTexts.Today(new AiUsageTotals(3, 1, 10000, 2000, 0, 0)));
        Assert.StartsWith("Today: 1,234 AI calls", AiUsageTexts.Today(new AiUsageTotals(1234, 0, 1, 1, 0, 0)));
        Assert.Throws<ArgumentNullException>(() => AiUsageTexts.Today(null!));
    }

    [Fact]
    public void Texts_Line()
    {
        var usage = new AiUsage(() => _ix, TestZones.Utc(), _time, _log);
        usage.Record(ModelCallKind.Draft, AiBackend.ClaudeCode, true, U);
        var ai = new AiDraftingSettings();

        Assert.Null(AiUsageTexts.Line(null, usage));
        Assert.Null(AiUsageTexts.Line(ai, usage));   // no consent
        ai.RecordConsent(_time.GetUtcNow());
        Assert.Null(AiUsageTexts.Line(ai, null));
        Assert.Equal(AiUsageTexts.Today(usage.Today()), AiUsageTexts.Line(ai, usage));
    }

    [Fact]
    public void Texts_UnknownCallsLogDetail()
    {
        Assert.Equal("", AiUsageTexts.UnknownCallsLogDetail(0));
        Assert.Equal("", AiUsageTexts.UnknownCallsLogDetail(-1));
        Assert.Equal(", 2 calls without token data", AiUsageTexts.UnknownCallsLogDetail(2));
    }
}
