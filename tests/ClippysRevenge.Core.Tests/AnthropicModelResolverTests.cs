using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class AnthropicModelResolverTests
{
    private const string Key = "test-api-key-0001";

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(logLevel + "|" + formatter(state, exception));
        }
    }

    private sealed class FakeLister
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Func<string, CancellationToken, Task<ModelListOutcome>> Impl { get; set; } =
            (_, _) => Task.FromResult<ModelListOutcome>(Listed(Opus6));

        public Task<ModelListOutcome> Invoke(string key, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Impl(key, ct);
        }
    }

    private static readonly DateTimeOffset D1 = new(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D2 = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset D3 = new(2027, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ModelListEntry Opus55 = new("claude-opus-5-5", D1, "opus", "active");
    private static readonly ModelListEntry Opus6 = new("claude-opus-6", D2, "opus", "active");
    private static readonly ModelListEntry Sonnet6 = new("claude-sonnet-6", D3, "sonnet", "active");

    private static ModelListOutcome Listed(params ModelListEntry[] e) => new ModelListOutcome.Listed(e);

    private readonly FakeLister _lister = new();
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLogger _log = new();

    private AnthropicModelResolver Resolver() => new(_lister.Invoke, _time, _log);

    private static Dictionary<string, JsonElement> Raw(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    // ---- SelectNewest ----

    [Fact]
    public void SelectNewest_PicksTheNewestOfTheLine()
    {
        ModelListEntry[] all = [Opus55, Opus6, Sonnet6];
        Assert.Equal("claude-opus-6", AnthropicModelResolver.SelectNewest(all, "opus"));
        Assert.Equal("claude-sonnet-6", AnthropicModelResolver.SelectNewest(all, "sonnet"));
        Assert.Null(AnthropicModelResolver.SelectNewest(all, "haiku"));
    }

    [Fact]
    public void SelectNewest_TieTakesTheGreatestId()
    {
        ModelListEntry[] all = [new("claude-opus-6a", D2, "opus", null), new("claude-opus-6b", D2, "opus", null)];
        Assert.Equal("claude-opus-6b", AnthropicModelResolver.SelectNewest(all, "opus"));
        Assert.Equal("claude-opus-6b", AnthropicModelResolver.SelectNewest(all.Reverse(), "opus"));
    }

    [Fact]
    public void SelectNewest_LineRules()
    {
        // No id inference.
        Assert.Null(AnthropicModelResolver.SelectNewest([new("claude-opus-9", D3, null, "active")], "opus"));
        Assert.Equal("x-model", AnthropicModelResolver.SelectNewest([new("x-model", D1, "opus", "active")], "opus"));
        Assert.Null(AnthropicModelResolver.SelectNewest([new("claude-opus-9", D3, "Opus", "active")], "opus"));
        // Retired is skipped, invalid ids are skipped.
        Assert.Equal("claude-opus-5-5", AnthropicModelResolver.SelectNewest(
            [Opus55, new("claude-opus-9", D3, "opus", "retired")], "opus"));
        Assert.Equal("claude-opus-5-5", AnthropicModelResolver.SelectNewest(
            [Opus55, new("-bad", D3, "opus", "active")], "opus"));
        // Deprecated still counts.
        Assert.Equal("claude-opus-9", AnthropicModelResolver.SelectNewest(
            [Opus55, new("claude-opus-9", D3, "opus", "deprecated")], "opus"));
    }

    [Fact]
    public void SelectNewest_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => AnthropicModelResolver.SelectNewest(null!, "opus"));
        Assert.Throws<ArgumentNullException>(() => AnthropicModelResolver.SelectNewest([], null!));
    }

    // ---- ModelListEntry.TryParse ----

    [Theory]
    [InlineData("""{"created_at":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"id":"","created_at":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"id":5,"created_at":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"id":"a"}""")]
    [InlineData("""{"id":"a","created_at":"yesterday"}""")]
    [InlineData("""{"id":"a","created_at":7}""")]
    public void TryParse_Invalid(string json)
    {
        Assert.False(ModelListEntry.TryParse(Raw(json), out var e));
        Assert.Null(e);
    }

    [Fact]
    public void TryParse_NullDictionary_IsFalse() => Assert.False(ModelListEntry.TryParse(null, out _));

    [Fact]
    public void TryParse_LineAndLifecycle_OptionalStrings()
    {
        Assert.True(ModelListEntry.TryParse(Raw("""{"id":"a","created_at":"1970-01-01T00:00:00Z"}"""), out var a));
        Assert.Null(a!.Line);
        Assert.Null(a.Lifecycle);
        Assert.Equal(DateTimeOffset.UnixEpoch, a.CreatedAt);

        Assert.True(ModelListEntry.TryParse(Raw("""{"id":"a","created_at":"2026-01-01T00:00:00Z","line":null,"lifecycle":3}"""), out var b));
        Assert.Null(b!.Line);
        Assert.Null(b.Lifecycle);

        Assert.True(ModelListEntry.TryParse(Raw("""{"id":"a","created_at":"2026-01-01T00:00:00Z","line":"opus","lifecycle":"retired"}"""), out var c));
        Assert.Equal("opus", c!.Line);
        Assert.Equal("retired", c.Lifecycle);
    }

    // ---- ResolveAsync ----

    [Fact]
    public async Task NonAlias_ResolvedAtOnce_NoCallNoLog()
    {
        var r = await Resolver().ResolveAsync("claude-opus-5-5", Key, false, TestContext.Current.CancellationToken);
        Assert.Equal(new ModelResolution("claude-opus-5-5", ModelResolutionKind.Resolved), r);
        Assert.Equal(0, _lister.Calls);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Cache_LastsTwentyFourHours()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = Resolver();
        var r = await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(new ModelResolution("claude-opus-6", ModelResolutionKind.Resolved), r);
        Assert.Equal(1, _lister.Calls);
        Assert.Equal(["Information|AI drafts: \"opus\" is claude-opus-6."], _log.Lines);

        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.Equal("claude-opus-6", (await res.ResolveAsync("opus", Key, false, ct)).Model);
        Assert.Equal(1, _lister.Calls);

        _time.Advance(TimeSpan.FromMinutes(1));
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(2, _lister.Calls);
    }

    [Fact]
    public async Task Aliases_AreCachedSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        _lister.Impl = (_, _) => Task.FromResult(Listed(Opus6, Sonnet6));
        var res = Resolver();
        Assert.Equal("claude-opus-6", (await res.ResolveAsync("opus", Key, false, ct)).Model);
        Assert.Equal("claude-sonnet-6", (await res.ResolveAsync("sonnet", Key, false, ct)).Model);
        Assert.Equal(2, _lister.Calls);
    }

    [Fact]
    public async Task Failure_HoldsForTenMinutes_AndWarnsOncePerEpisode()
    {
        var ct = TestContext.Current.CancellationToken;
        _lister.Impl = (_, _) => Task.FromResult<ModelListOutcome>(new ModelListOutcome.Failed("HTTP 500"));
        var res = Resolver();
        var fallback = new ModelResolution("claude-opus-5-5", ModelResolutionKind.Fallback);
        const string warning = "Warning|AI drafts: couldn't look up the newest \"opus\" model (HTTP 500); using claude-opus-5-5.";

        Assert.Equal(fallback, await res.ResolveAsync("opus", Key, false, ct));
        Assert.Equal([warning], _log.Lines);

        _time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(fallback, await res.ResolveAsync("opus", Key, false, ct));
        Assert.Equal(1, _lister.Calls);
        Assert.Single(_log.Lines);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(fallback, await res.ResolveAsync("opus", Key, false, ct));
        Assert.Equal(2, _lister.Calls);
        Assert.Single(_log.Lines);

        _lister.Impl = (_, _) => Task.FromResult(Listed(Opus6));
        _time.Advance(AnthropicModelResolver.RetryAfterFailure);
        Assert.Equal("claude-opus-6", (await res.ResolveAsync("opus", Key, false, ct)).Model);
        Assert.Equal("Information|AI drafts: \"opus\" is claude-opus-6.", _log.Lines[^1]);
        Assert.Equal(2, _log.Lines.Count);

        _lister.Impl = (_, _) => Task.FromResult<ModelListOutcome>(new ModelListOutcome.Failed("HTTP 500"));
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(fallback, await res.ResolveAsync("opus", Key, false, ct));
        Assert.Equal(warning, _log.Lines[^1]);
        Assert.Equal(3, _log.Lines.Count);
    }

    [Fact]
    public async Task ListedWithoutMatch_IsAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        _lister.Impl = (_, _) => Task.FromResult(Listed());
        var res = Resolver();
        Assert.Equal(ModelResolutionKind.Fallback, (await res.ResolveAsync("opus", Key, false, ct)).Kind);
        Assert.Equal(["Warning|AI drafts: couldn't look up the newest \"opus\" model (no model of that family); using claude-opus-5-5."],
            _log.Lines);
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(1, _lister.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Rejected_NoHoldNoLog(int status)
    {
        var ct = TestContext.Current.CancellationToken;
        _lister.Impl = (_, _) => Task.FromResult<ModelListOutcome>(new ModelListOutcome.Rejected(status));
        var res = Resolver();
        Assert.Equal(new ModelResolution("", ModelResolutionKind.Rejected, status), await res.ResolveAsync("opus", Key, false, ct));
        Assert.Empty(_log.Lines);
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(2, _lister.Calls);
    }

    [Fact]
    public async Task Fresh_IgnoresCacheAndHold_AndAFailureDropsTheCachedId()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = Resolver();
        await res.ResolveAsync("opus", Key, false, ct);
        await res.ResolveAsync("opus", Key, true, ct);
        Assert.Equal(2, _lister.Calls);

        _lister.Impl = (_, _) => Task.FromResult<ModelListOutcome>(new ModelListOutcome.Failed("HTTP 500"));
        Assert.Equal(ModelResolutionKind.Fallback, (await res.ResolveAsync("opus", Key, true, ct)).Kind);
        Assert.Equal(3, _lister.Calls);
        var after = await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(new ModelResolution("claude-opus-5-5", ModelResolutionKind.Fallback), after);
        Assert.Equal(3, _lister.Calls);

        // During the hold a fresh call still lists.
        await res.ResolveAsync("opus", Key, true, ct);
        Assert.Equal(4, _lister.Calls);
    }

    [Fact]
    public async Task Cancellation_ThrowsAndStoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lister.Impl = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Listed();
        };
        var res = Resolver();
        var pending = res.ResolveAsync("opus", Key, false, cts.Token);
        await entered.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(_log.Lines);

        _lister.Impl = (_, _) => Task.FromResult(Listed(Opus6));
        Assert.Equal("claude-opus-6", (await res.ResolveAsync("opus", Key, false, ct)).Model);
        Assert.Equal(2, _lister.Calls);
    }

    [Fact]
    public async Task Invalidate_DropsCacheHoldAndEpisode()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = Resolver();
        await res.ResolveAsync("opus", Key, false, ct);
        res.Invalidate();
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(2, _lister.Calls);

        _lister.Impl = (_, _) => Task.FromResult<ModelListOutcome>(new ModelListOutcome.Failed("HTTP 500"));
        res.Invalidate();
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(3, _lister.Calls);
        var warnings = _log.Lines.Count(l => l.StartsWith("Warning|", StringComparison.Ordinal));
        Assert.Equal(1, warnings);

        res.Invalidate();   // hold gone, episode closed
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(4, _lister.Calls);
        Assert.Equal(2, _log.Lines.Count(l => l.StartsWith("Warning|", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Invalidate_DuringALookup_StoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<ModelListOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lister.Impl = (_, _) =>
        {
            entered.TrySetResult();
            return gate.Task;
        };
        var res = Resolver();
        var pending = res.ResolveAsync("opus", Key, false, ct);
        await entered.Task;
        res.Invalidate();
        gate.SetResult(Listed(Opus6));
        Assert.Equal("claude-opus-6", (await pending).Model);

        _lister.Impl = (_, _) => Task.FromResult(Listed(Opus6));
        await res.ResolveAsync("opus", Key, false, ct);
        Assert.Equal(2, _lister.Calls);
    }

    [Fact]
    public async Task SingleFlight_TwoConcurrentCallsListOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<ModelListOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lister.Impl = (_, _) =>
        {
            entered.TrySetResult();
            return gate.Task;
        };
        var res = Resolver();
        var a = res.ResolveAsync("opus", Key, false, ct);
        await entered.Task;
        var b = res.ResolveAsync("opus", Key, false, ct);
        gate.SetResult(Listed(Opus6));
        var results = await Task.WhenAll(a, b);
        Assert.All(results, r => Assert.Equal(new ModelResolution("claude-opus-6", ModelResolutionKind.Resolved), r));
        Assert.Equal(1, _lister.Calls);
    }

    [Fact]
    public async Task ListerThrowing_IsNoAnswer()
    {
        _lister.Impl = (_, _) => throw new InvalidOperationException("boom");
        var r = await Resolver().ResolveAsync("opus", Key, false, TestContext.Current.CancellationToken);
        Assert.Equal(ModelResolutionKind.Fallback, r.Kind);
        Assert.Equal(["Warning|AI drafts: couldn't look up the newest \"opus\" model (no answer); using claude-opus-5-5."], _log.Lines);
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new AnthropicModelResolver(null!));
        var res = Resolver();
        await Assert.ThrowsAsync<ArgumentNullException>(() => res.ResolveAsync(null!, Key, false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => res.ResolveAsync("opus", null!, false, CancellationToken.None));
    }
}
