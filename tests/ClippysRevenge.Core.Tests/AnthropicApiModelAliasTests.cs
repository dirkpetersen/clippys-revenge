using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

/// <summary>SPEC 19.3 "Model aliases": the real AnthropicApiModel over the fake HTTP handler. No network.</summary>
public class AnthropicApiModelAliasTests
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

    private readonly FakeAnthropicHandler _fake = new();
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLogger _log = new();
    private readonly AiDraftingSettings _settings = new() { Model = "opus" };
    private Func<string?> _key = () => Key;

    // Behavior of the /v1/models route; /v1/messages always answers with the reply.
    private Func<FakeAnthropicHandler.Recorded, CancellationToken, Task<HttpResponseMessage>> _list;
    private Func<FakeAnthropicHandler.Recorded, CancellationToken, Task<HttpResponseMessage>> _messages =
        (_, _) => Task.FromResult(FakeAnthropicHandler.Json(200, FakeAnthropicHandler.OkBody(FakeAnthropicHandler.Reply)));

    public AnthropicApiModelAliasTests()
    {
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(200, Page([Entry("claude-opus-5-5", "2026-09-21T00:00:00Z", "opus"),
            Entry("claude-opus-6", "2027-03-01T00:00:00Z", "opus")], false, "claude-opus-6")));
        _fake.Responder = (rec, ct) => rec.Uri.AbsolutePath == "/v1/models" ? _list(rec, ct) : _messages(rec, ct);
    }

    private AnthropicApiModel Model() => new(() => _settings, () => _key(), _fake, _time, _log);

    private static ModelRequest Req(string model = "opus") =>
        new("SYS", "USER", DraftModel.ReplySchema, model, "medium", TimeSpan.FromSeconds(180), DraftModel.DraftMaxOutputTokens);

    private static string Entry(string id, string created, string? line, string lifecycle = "active") =>
        $$"""{"type":"model","id":"{{id}}","display_name":"x","created_at":"{{created}}","line":{{(line is null ? "null" : "\"" + line + "\"")}},"lifecycle":"{{lifecycle}}"}""";

    private static string Page(string[] entries, bool hasMore, string lastId) =>
        $$"""{"data":[{{string.Join(",", entries)}}],"first_id":"a","has_more":{{(hasMore ? "true" : "false")}},"last_id":"{{lastId}}"}""";

    private static string ModelOf(FakeAnthropicHandler.Recorded r) =>
        JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString()!;

    private IReadOnlyList<FakeAnthropicHandler.Recorded> Lists => [.. _fake.Requests.Where(r => r.Uri.AbsolutePath == "/v1/models")];
    private IReadOnlyList<FakeAnthropicHandler.Recorded> Posts => [.. _fake.Requests.Where(r => r.Uri.AbsolutePath == "/v1/messages")];

    [Fact]
    public async Task Run_Alias_ListsThenUsesTheNewestId()
    {
        using var m = Model();
        var r = await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.IsType<ModelResult.Ok>(r);
        Assert.Equal(2, _fake.Calls);
        var list = _fake.Requests[0];
        Assert.Equal(HttpMethod.Get, list.Method);
        Assert.StartsWith("https://api.anthropic.com/v1/models", list.Uri.ToString());
        Assert.Contains("limit=100", list.Uri.Query);
        var post = _fake.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("claude-opus-6", ModelOf(post));
        foreach (var rec in _fake.Requests)
        {
            Assert.Equal("Bearer " + Key, rec.Headers["Authorization"]);
            Assert.False(rec.Headers.ContainsKey("x-api-key"));
        }
    }

    [Fact]
    public async Task Run_SecondCall_UsesTheCache()
    {
        using var m = Model();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal(3, _fake.Calls);
        Assert.Single(Lists);
        Assert.Equal(2, Posts.Count);
    }

    [Fact]
    public async Task Run_NonAlias_NeverLists()
    {
        using var m = Model();
        await m.RunAsync(Req("claude-sonnet-5-5"), TestContext.Current.CancellationToken);
        Assert.Empty(Lists);
        Assert.Equal("claude-sonnet-5-5", ModelOf(Posts[0]));
    }

    [Fact]
    public async Task Run_Paging_FollowsAfterId()
    {
        _list = (rec, _) => Task.FromResult(FakeAnthropicHandler.Json(200, rec.Uri.Query.Contains("after_id=claude-opus-5-5", StringComparison.Ordinal)
            ? Page([Entry("claude-opus-6", "2027-03-01T00:00:00Z", "opus")], false, "claude-opus-6")
            : Page([Entry("claude-opus-5-5", "2026-09-21T00:00:00Z", "opus")], true, "claude-opus-5-5")));
        using var m = Model();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal(2, Lists.Count);
        Assert.Contains("after_id=claude-opus-5-5", Lists[1].Uri.Query);
        Assert.Equal("claude-opus-6", ModelOf(Posts[0]));
    }

    [Fact]
    public async Task Run_PageCap_StopsAtTenPages()
    {
        int n = 0;
        _list = (_, _) =>
        {
            int i = Interlocked.Increment(ref n);
            return Task.FromResult(FakeAnthropicHandler.Json(200,
                Page([Entry("claude-opus-5-" + i, $"2026-09-{i:00}T00:00:00Z", "opus")], true, "claude-opus-5-" + i)));
        };
        using var m = Model();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal(AnthropicModelResolver.MaxPages, Lists.Count);
        Assert.Equal("claude-opus-5-10", ModelOf(Posts[0]));
    }

    [Theory]
    [InlineData(500, "HTTP 500")]
    [InlineData(429, "HTTP 429")]
    [InlineData(404, "HTTP 404")]
    [InlineData(0, "no answer")]
    public async Task Run_ListFails_FallsBackAndWarnsOnce(int status, string kind)
    {
        _list = status == 0
            ? (_, _) => throw new HttpRequestException("boom")
            : (_, _) => Task.FromResult(FakeAnthropicHandler.Json(status, """{"type":"error","error":{"type":"api_error","message":"x"}}"""));
        using var m = Model();
        var ct = TestContext.Current.CancellationToken;
        Assert.IsType<ModelResult.Ok>(await m.RunAsync(Req(), ct));
        Assert.Equal("claude-opus-5-5", ModelOf(Posts[0]));
        var warnings = _log.Lines.Where(l => l.StartsWith("Warning|", StringComparison.Ordinal)).ToList();
        Assert.Single(warnings);
        Assert.Contains($"({kind})", warnings[0]);

        await m.RunAsync(Req(), ct);   // held: no new list, no new warning
        Assert.Single(Lists);
        Assert.Single(_log.Lines, l => l.StartsWith("Warning|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_UnreadableList_FallsBack()
    {
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(200, "not json"));
        using var m = Model();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal("claude-opus-5-5", ModelOf(Posts[0]));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning|", StringComparison.Ordinal) && l.Contains("(unreadable answer)"));
    }

    [Fact]
    public async Task Run_ListTimeout_FallsBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _list = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return FakeAnthropicHandler.Json(200, "{}");
        };
        using var m = Model();
        var run = m.RunAsync(Req(), ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        _time.Advance(AnthropicApiModel.CheckTimeout + TimeSpan.FromMilliseconds(1));
        Assert.IsType<ModelResult.Ok>(await run.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal("claude-opus-5-5", ModelOf(Posts[0]));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning|", StringComparison.Ordinal) && l.Contains("(timed out)"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Run_ListRejected_IsNotSignedIn(int status)
    {
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(status, """{"type":"error","error":{"type":"authentication_error","message":"x"}}"""));
        using var m = Model();
        Assert.IsType<ModelResult.NotSignedIn>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Empty(Posts);
    }

    [Fact]
    public async Task Run_CancelWhileListing_IsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _list = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return FakeAnthropicHandler.Json(200, "{}");
        };
        using var m = Model();
        var run = m.RunAsync(Req(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        cts.Cancel();
        Assert.Equal(new ModelResult.Failed(ModelFailure.Cancelled, null), await run.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Empty(Posts);
    }

    [Fact]
    public async Task Run_NoKey_NoRequests()
    {
        _key = () => null;
        using var m = Model();
        Assert.IsType<ModelResult.NotSignedIn>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(0, _fake.Calls);
    }

    [Fact]
    public async Task Run_Messages404AfterFallback_IsHttpFailure()
    {
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(500, "{}"));
        _messages = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(404, """{"type":"error","error":{"type":"not_found_error","message":"x"}}"""));
        using var m = Model();
        Assert.Equal(new ModelResult.Failed(ModelFailure.Http, 404), await m.RunAsync(Req(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Check_Alias_FreshListing_NoRetrieve()
    {
        var ct = TestContext.Current.CancellationToken;
        using var m = Model();
        var s = await m.CheckAsync(ct);
        Assert.Equal(new ModelStatus(true, "✓ API key works (opus → claude-opus-6)"), s);
        Assert.All(_fake.Requests, r => Assert.Equal("/v1/models", r.Uri.AbsolutePath));
        Assert.DoesNotContain(_fake.Requests, r => r.Uri.AbsolutePath.StartsWith("/v1/models/", StringComparison.Ordinal));

        await m.CheckAsync(ct);
        Assert.Equal(2, Lists.Count);

        await m.RunAsync(Req(), ct);   // the Check filled the cache
        Assert.Equal(2, Lists.Count);
        Assert.Single(Posts);
    }

    [Fact]
    public async Task Check_Alias_Failures()
    {
        var ct = TestContext.Current.CancellationToken;
        using var m = Model();
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(500, "{}"));
        Assert.Equal(new ModelStatus(false, "✗ api.anthropic.com didn't answer."), await m.CheckAsync(ct));
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(401, "{}"));
        Assert.Equal(new ModelStatus(false, "✗ The API key was rejected (HTTP 401)."), await m.CheckAsync(ct));

        _key = () => null;
        var before = _fake.Calls;
        Assert.Equal(new ModelStatus(false, "✗ No API key saved."), await m.CheckAsync(ct));
        Assert.Equal(before, _fake.Calls);
    }

    [Fact]
    public async Task Check_NonAlias_StillRetrievesTheModel()
    {
        _settings.Model = "claude-opus-5-5";
        _fake.Responder = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(200, """{"id":"claude-opus-5-5","type":"model"}"""));
        using var m = Model();
        Assert.True((await m.CheckAsync(TestContext.Current.CancellationToken)).Ready);
        Assert.Equal("https://api.anthropic.com/v1/models/claude-opus-5-5", _fake.Last.Uri.ToString());
    }

    [Fact]
    public async Task InvalidateModelCache_ForcesANewListing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var m = Model();
        await m.RunAsync(Req(), ct);
        m.InvalidateModelCache();
        await m.RunAsync(Req(), ct);
        Assert.Equal(2, Lists.Count);
    }

    [Fact]
    public async Task Run_StyleRequest_ResolvesTheAliasToo()
    {
        using var m = Model();
        var req = Req() with { JsonSchema = DraftModel.StyleSchema, MaxOutputTokens = 16000 };
        await m.RunAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal("claude-opus-6", ModelOf(Posts[0]));
    }

    [Fact]
    public async Task Logs_NeverHoldKeyPromptsOrReply()
    {
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(500, "{}"));
        using var m = Model();
        var ct = TestContext.Current.CancellationToken;
        await m.RunAsync(Req(), ct);
        m.InvalidateModelCache();
        _list = (_, _) => Task.FromResult(FakeAnthropicHandler.Json(200, Page([Entry("claude-opus-6", "2027-03-01T00:00:00Z", "opus")], false, "x")));
        await m.RunAsync(Req(), ct);
        await m.CheckAsync(ct);
        Assert.NotEmpty(_log.Lines);
        foreach (var line in _log.Lines)
            foreach (var secret in new[] { "test-api-key", "SYS", "USER", "<p>Hi</p>", "needs_reply" })
                Assert.DoesNotContain(secret, line);
    }
}
