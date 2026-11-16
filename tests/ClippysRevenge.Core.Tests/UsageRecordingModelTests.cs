using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class UsageRecordingModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-urm-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeDraftModel _inner = new();
    private readonly CapturingLog _log = new();
    private readonly AiDraftingSettings _settings = new();

    public UsageRecordingModelTests() => _ix = new EmailIndex(Path.Combine(_dir, "index.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private AiUsage Usage(Func<EmailIndex>? own = null) =>
        new(own ?? (() => _ix), TestZones.Utc(), _time, _log);

    private UsageRecordingModel Wrap(AiUsage usage) => new(_inner, () => _settings, usage, _log);

    private static ModelRequest Req(ModelCallKind kind = ModelCallKind.Draft) =>
        new("SYS-SECRET", "USER-SECRET", DraftModel.ReplySchema, "m", "medium", TimeSpan.FromSeconds(10), 100, kind);

    private static ModelResult.Ok OkWith(TokenUsage? usage) =>
        new(JsonDocument.Parse("{}").RootElement.Clone(), TimeSpan.FromSeconds(1), 1, 1, usage);

    private List<(string kind, string backend, string outcome, long? i, long? o, long? cr, long? cc)> Rows()
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_dir, "index.db"), Pooling = false }.ToString());
        c.Open();
        return c.Query<(string, string, string, long?, long?, long?, long?)>(
            "SELECT kind, backend, outcome, input, output, cache_read, cache_creation FROM ai_calls ORDER BY id").ToList();
    }

    [Fact]
    public async Task Ok_is_recorded_and_the_same_instance_is_returned()
    {
        var usage = Usage();
        var inner = OkWith(new TokenUsage(10, 5, 100, 0));
        _inner.Enqueue(inner);
        _settings.Backend = AiBackend.ClaudeCode;

        var r = await Wrap(usage).RunAsync(Req(ModelCallKind.Manual), TestContext.Current.CancellationToken);

        Assert.Same(inner, r);
        Assert.Equal([("manual", "cli", "ok", 10L, 5L, 100L, 0L)], Rows());
        Assert.Equal(new AiUsageTotals(1, 0, 10, 5, 100, 0), usage.Today());
    }

    [Fact]
    public async Task Api_backend_is_recorded_as_api()
    {
        _inner.Enqueue(OkWith(new TokenUsage(1, 1, null, null)));
        _settings.Backend = AiBackend.ApiKey;
        await Wrap(Usage()).RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal("api", Assert.Single(Rows()).backend);
    }

    [Theory]
    [InlineData(ModelFailure.Timeout)]
    [InlineData(ModelFailure.BadOutput)]
    [InlineData(ModelFailure.NoStructuredOutput)]
    [InlineData(ModelFailure.ProcessExit)]
    [InlineData(ModelFailure.Http)]
    public async Task Billed_failures_are_recorded(ModelFailure kind)
    {
        _inner.Enqueue(new ModelResult.Failed(kind, null, new TokenUsage(7, 8, null, null)),
            new ModelResult.Failed(kind, null, null));
        var m = Wrap(Usage());
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal([("draft", "cli", "failed", 7L, 8L, null, null), ("draft", "cli", "failed", null, null, null, null)],
            Rows().Select(r => (r.kind, r.backend, r.outcome, r.i, r.o, r.cr, r.cc)).ToArray());
    }

    [Fact]
    public async Task Unbilled_results_are_not_recorded()
    {
        var usage = Usage();
        _inner.Enqueue(new ModelResult.RateLimited(null), new ModelResult.NotSignedIn(),
            new ModelResult.Failed(ModelFailure.NotInstalled, null), new ModelResult.Failed(ModelFailure.BadInput, null),
            new ModelResult.Failed(ModelFailure.Cancelled, null));
        var m = Wrap(usage);
        for (var i = 0; i < 5; i++) await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Empty(Rows());
        Assert.Equal(0, usage.Today().Calls);
    }

    [Fact]
    public async Task Every_kind_passes_through()
    {
        var m = Wrap(Usage());
        foreach (var k in Enum.GetValues<ModelCallKind>())
        {
            _inner.Enqueue(OkWith(null));
            await m.RunAsync(Req(k), TestContext.Current.CancellationToken);
        }
        Assert.Equal(["draft", "manual", "revisit", "rescan", "style"], Rows().Select(r => r.kind));
    }

    [Fact]
    public async Task A_failing_insert_never_changes_the_result_and_logs_the_type_only()
    {
        var usage = Usage(() => throw new InvalidOperationException("SECRET-DETAIL"));
        var inner = OkWith(new TokenUsage(1, 2, null, null));
        _inner.Enqueue(inner);

        var r = await Wrap(usage).RunAsync(Req(), TestContext.Current.CancellationToken);

        Assert.Same(inner, r);
        var line = Assert.Single(_log.Lines);
        Assert.Contains("InvalidOperationException", line);
        Assert.DoesNotContain("SECRET", line);
        Assert.Equal(1, usage.Today().Calls);
    }

    [Fact]
    public async Task Check_passes_through_and_records_nothing()
    {
        var m = Wrap(Usage());
        var s = await m.CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(s.Ready);
        Assert.Equal(1, _inner.CheckCalls);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task A_throwing_settings_func_skips_recording_with_a_warning()
    {
        var usage = Usage();
        _inner.Enqueue(OkWith(new TokenUsage(1, 1, null, null)));
        var m = new UsageRecordingModel(_inner, () => throw new InvalidOperationException("x"), usage, _log);
        var r = await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.IsType<ModelResult.Ok>(r);
        Assert.Empty(Rows());
        Assert.Contains("InvalidOperationException", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task Null_request_throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Wrap(Usage()).RunAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IsRecorded_and_UsageOf_table()
    {
        var u = new TokenUsage(1, 2, null, null);
        Assert.False(UsageRecordingModel.IsRecorded(null));
        Assert.Null(UsageRecordingModel.UsageOf(null));
        Assert.True(UsageRecordingModel.IsRecorded(OkWith(null)));
        Assert.Same(u, UsageRecordingModel.UsageOf(OkWith(u)));
        Assert.False(UsageRecordingModel.IsRecorded(new ModelResult.RateLimited(null)));
        Assert.False(UsageRecordingModel.IsRecorded(new ModelResult.NotSignedIn()));
        Assert.Null(UsageRecordingModel.UsageOf(new ModelResult.RateLimited(null)));
        foreach (var f in Enum.GetValues<ModelFailure>())
        {
            var expected = f is ModelFailure.Timeout or ModelFailure.BadOutput or ModelFailure.NoStructuredOutput
                or ModelFailure.ProcessExit or ModelFailure.Http;
            Assert.Equal(expected, UsageRecordingModel.IsRecorded(new ModelResult.Failed(f, null, u)));
            Assert.Same(u, UsageRecordingModel.UsageOf(new ModelResult.Failed(f, null, u)));
        }
    }
}
