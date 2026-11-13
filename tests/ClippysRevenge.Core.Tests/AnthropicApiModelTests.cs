using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class AnthropicApiModelTests
{
    private const string Key1 = "test-api-key-0001";
    private const string Key2 = "test-api-key-0002";

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
    private readonly AiDraftingSettings _settings = new();
    private Func<string?> _key = () => Key1;
    private int _keyCalls;

    private AnthropicApiModel Model() =>
        new(() => _settings, () => { Interlocked.Increment(ref _keyCalls); return _key(); }, _fake, _time, _log);

    private static ModelRequest Req() =>
        new("SYS", "USER", DraftModel.ReplySchema, "claude-opus-5-5", "medium", TimeSpan.FromSeconds(180), DraftModel.DraftMaxOutputTokens);

    private static JsonElement Body(FakeAnthropicHandler.Recorded r) => JsonDocument.Parse(r.Body).RootElement;

    private static JsonElement Reduced(string schema)
    {
        Assert.True(AnthropicWireSchema.TryReduce(schema, out var d));
        return JsonSerializer.SerializeToElement(d);
    }

    private static void AssertFailed(ModelResult r, ModelFailure kind, int? code = null) =>
        Assert.Equal(new ModelResult.Failed(kind, code), r);

    // ---- RunAsync ----

    [Fact]
    public async Task Ok_ParsesOutputAndTokens()
    {
        using var m = Model();
        var r = await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        var ok = Assert.IsType<ModelResult.Ok>(r);
        Assert.Equal(0.8, ok.Output.GetProperty("confidence").GetDouble());
        Assert.Equal(120, ok.InputTokens);
        Assert.Equal(45, ok.OutputTokens);
        Assert.Equal(1, _fake.Calls);
    }

    [Fact]
    public async Task RequestShape_AndHeaders()
    {
        using var m = Model();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        var rec = _fake.Last;
        Assert.Equal(HttpMethod.Post, rec.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", rec.Uri.ToString());
        var b = Body(rec);
        Assert.Equal(new HashSet<string> { "model", "max_tokens", "system", "messages", "output_config" },
            b.EnumerateObject().Select(p => p.Name).ToHashSet());
        Assert.Equal("claude-opus-5-5", b.GetProperty("model").GetString());
        Assert.Equal(8000, b.GetProperty("max_tokens").GetInt32());
        Assert.Equal("SYS", b.GetProperty("system").GetString());
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse("""[{"role":"user","content":"USER"}]""").RootElement,
            b.GetProperty("messages")));
        var oc = b.GetProperty("output_config");
        Assert.Equal("medium", oc.GetProperty("effort").GetString());
        Assert.Equal("json_schema", oc.GetProperty("format").GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(Reduced(DraftModel.ReplySchema), oc.GetProperty("format").GetProperty("schema")));

        Assert.Equal("Bearer " + Key1, rec.Headers["Authorization"]);
        Assert.False(rec.Headers.ContainsKey("x-api-key"));
        Assert.True(rec.Headers.ContainsKey("anthropic-version"));
    }

    [Fact]
    public async Task RequestValues_FollowTheRequest()
    {
        using var m = Model();
        await m.RunAsync(Req() with
        {
            Effort = "low", MaxOutputTokens = 16000, JsonSchema = DraftModel.StyleSchema, Model = "claude-sonnet-5",
        }, TestContext.Current.CancellationToken);
        var b = Body(_fake.Last);
        Assert.Equal("claude-sonnet-5", b.GetProperty("model").GetString());
        Assert.Equal(16000, b.GetProperty("max_tokens").GetInt32());
        Assert.Equal("low", b.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.True(JsonElement.DeepEquals(Reduced(DraftModel.StyleSchema),
            b.GetProperty("output_config").GetProperty("format").GetProperty("schema")));
    }

    [Fact]
    public async Task WhitespaceSystemPrompt_IsOmitted()
    {
        using var m = Model();
        await m.RunAsync(Req() with { SystemPrompt = "   " }, TestContext.Current.CancellationToken);
        Assert.False(Body(_fake.Last).TryGetProperty("system", out _));
    }

    [Fact]
    public async Task ThinkingBlock_IsSkipped()
    {
        var content = $$"""[{"type":"thinking","thinking":"t","signature":"s"},{"type":"text","text":{{JsonSerializer.Serialize(FakeAnthropicHandler.Reply)}}}]""";
        _fake.Respond(200, FakeAnthropicHandler.OkContent(content));
        using var m = Model();
        Assert.IsType<ModelResult.Ok>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TextBlocks_AreConcatenated()
    {
        var content = $$"""[{"type":"text","text":{{JsonSerializer.Serialize("{\"needs_reply\":false,")}}},{"type":"text","text":{{JsonSerializer.Serialize("\"confidence\":0.1,\"reason\":\"r\"}")}}}]""";
        _fake.Respond(200, FakeAnthropicHandler.OkContent(content));
        using var m = Model();
        var ok = Assert.IsType<ModelResult.Ok>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.False(ok.Output.GetProperty("needs_reply").GetBoolean());
    }

    [Fact]
    public async Task NonJsonText_IsNoStructuredOutput()
    {
        _fake.Respond(200, FakeAnthropicHandler.OkBody("I can't help with that."));
        using var m = Model();
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.NoStructuredOutput);
    }

    [Fact]
    public async Task ArrayOrEmptyContent_IsNoStructuredOutput()
    {
        using var m = Model();
        _fake.Respond(200, FakeAnthropicHandler.OkBody("[1]"));
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.NoStructuredOutput);
        _fake.Respond(200, FakeAnthropicHandler.OkContent("[]"));
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.NoStructuredOutput);
    }

    [Fact]
    public async Task Refusal_IsNoStructuredOutput()
    {
        _fake.Respond(200, FakeAnthropicHandler.OkBody(FakeAnthropicHandler.Reply, "refusal"));
        using var m = Model();
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.NoStructuredOutput);
    }

    [Fact]
    public async Task MaxTokens_IsBadOutput()
    {
        _fake.Respond(200, FakeAnthropicHandler.OkBody("{\"needs_reply\":tr", "max_tokens"));
        using var m = Model();
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.BadOutput);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task UnreadableBody_IsBadOutput(string body)
    {
        _fake.Respond(200, body);
        using var m = Model();
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.BadOutput);
    }

    [Fact]
    public async Task HugeTokenCount_BecomesNull()
    {
        _fake.Respond(200, FakeAnthropicHandler.OkBody(FakeAnthropicHandler.Reply, inTok: 3_000_000_000));
        using var m = Model();
        var ok = Assert.IsType<ModelResult.Ok>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Null(ok.InputTokens);
        Assert.Equal(45, ok.OutputTokens);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthFailures_AreNotSignedIn(int status)
    {
        _fake.Respond(status, """{"type":"error","error":{"type":"authentication_error","message":"BODYMARKER"}}""");
        using var m = Model();
        Assert.IsType<ModelResult.NotSignedIn>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(1, _fake.Calls);
    }

    [Theory]
    [InlineData(429, "30", 30)]
    [InlineData(529, "120", 120)]
    public async Task RateLimited_UsesRetryAfterSeconds(int status, string header, int seconds)
    {
        _fake.Respond(status, "{}", ("retry-after", header));
        using var m = Model();
        var r = Assert.IsType<ModelResult.RateLimited>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(ManualTimeProvider.T0.AddSeconds(seconds), r.RetryAt);
        Assert.Equal(1, _fake.Calls);
    }

    [Fact]
    public async Task RateLimited_UsesRetryAfterDate()
    {
        _fake.Respond(429, "{}", ("retry-after", "Mon, 15 Jun 2026 13:00:00 GMT"));
        using var m = Model();
        var r = Assert.IsType<ModelResult.RateLimited>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero), r.RetryAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("864000")]
    public async Task RateLimited_UnknownOrTooFar_IsNull(string? header)
    {
        if (header is null) _fake.Respond(429, "{}");
        else _fake.Respond(429, "{}", ("retry-after", header));
        using var m = Model();
        var r = Assert.IsType<ModelResult.RateLimited>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Null(r.RetryAt);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(413)]
    public async Task OtherHttpErrors_AreHttp(int status)
    {
        _fake.Respond(status, "{}");
        using var m = Model();
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.Http, status);
        Assert.Equal(1, _fake.Calls);
    }

    [Fact]
    public async Task NetworkError_IsHttpNull()
    {
        _fake.Throw();
        using var m = Model();
        AssertFailed(await m.RunAsync(Req(), TestContext.Current.CancellationToken), ModelFailure.Http);
        Assert.Equal(1, _fake.Calls);
    }

    [Fact]
    public async Task Hang_DeadlineGivesTimeout()
    {
        _fake.Hang();
        using var m = Model();
        var t = m.RunAsync(Req(), TestContext.Current.CancellationToken);
        await _fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(180) + TimeSpan.FromMilliseconds(1));
        AssertFailed(await t.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), ModelFailure.Timeout);
    }

    [Fact]
    public async Task Hang_CancelGivesCancelled()
    {
        _fake.Hang();
        using var m = Model();
        using var cts = new CancellationTokenSource();
        var t = m.RunAsync(Req(), cts.Token);
        await _fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cts.Cancel();
        AssertFailed(await t.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), ModelFailure.Cancelled);
    }

    [Fact]
    public async Task AlreadyCancelled_NoKeyLoadNoCall()
    {
        using var m = Model();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        AssertFailed(await m.RunAsync(Req(), cts.Token), ModelFailure.Cancelled);
        Assert.Equal(0, _fake.Calls);
        Assert.Equal(0, _keyCalls);
    }

    public static IEnumerable<object[]> BadInputs()
    {
        yield return [Req() with { Model = "--bare" }];
        yield return [Req() with { Effort = "ultra" }];
        yield return [Req() with { JsonSchema = "[]" }];
        yield return [Req() with { Timeout = TimeSpan.Zero }];
        yield return [Req() with { MaxOutputTokens = 0 }];
        yield return [Req() with { MaxOutputTokens = 32_001 }];
        yield return [Req() with { UserPrompt = "" }];
        yield return [Req() with { SystemPrompt = null! }];
        yield return [Req() with { UserPrompt = new string('a', 2_097_153) }];
    }

    [Theory]
    [MemberData(nameof(BadInputs))]
    public async Task BadInput_NoKeyLoadNoCall(ModelRequest req)
    {
        using var m = Model();
        AssertFailed(await m.RunAsync(req, TestContext.Current.CancellationToken), ModelFailure.BadInput);
        Assert.Equal(0, _fake.Calls);
        Assert.Equal(0, _keyCalls);
    }

    [Fact]
    public async Task Request_Null_Throws()
    {
        using var m = Model();
        await Assert.ThrowsAsync<ArgumentNullException>(() => m.RunAsync(null!, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("a b")]
    [InlineData("THROW")]
    public async Task NoUsableKey_IsNotSignedIn_NoCall(string? key)
    {
        _key = () => key == "THROW" ? throw new IOException("x") : key;
        using var m = Model();
        Assert.IsType<ModelResult.NotSignedIn>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(0, _fake.Calls);
    }

    [Fact]
    public async Task Key_IsReadPerCall()
    {
        using var m = Model();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        _key = () => Key2;
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        Assert.Equal("Bearer " + Key2, _fake.Last.Headers["Authorization"]);
        Assert.Equal(2, _keyCalls);
    }

    [Fact]
    public async Task SharedHandler_IsNeverDisposed()
    {
        var m = Model();
        for (var i = 0; i < 3; i++) Assert.IsType<ModelResult.Ok>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(3, _fake.Calls);
        Assert.False(_fake.Disposed);
        m.Dispose();
        m.Dispose();
        Assert.False(_fake.Disposed);
    }

    [Fact]
    public async Task ParallelCalls_AllSucceed()
    {
        using var m = Model();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => m.RunAsync(Req(), TestContext.Current.CancellationToken)));
        Assert.All(results, r => Assert.IsType<ModelResult.Ok>(r));
        Assert.Equal(5, _fake.Calls);
    }

    [Fact]
    public async Task Logs_NeverContainSecrets()
    {
        using var m = Model();
        // Ok
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        // 401
        _fake.Respond(401, """{"error":{"message":"BODYMARKER"}}""");
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        // 429
        _fake.Respond(429, "{}", ("retry-after", "30"));
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        // network
        _fake.Throw();
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);
        // no key
        _key = () => null;
        await m.RunAsync(Req(), TestContext.Current.CancellationToken);

        Assert.Equal(5, _log.Lines.Count);
        foreach (var line in _log.Lines)
        {
            Assert.StartsWith("Information|Anthropic API call: ", line);
            foreach (var secret in new[] { "test-api-key", "SYS", "USER", "<p>Hi</p>", "BODYMARKER", "claude-opus-5-5" })
                Assert.DoesNotContain(secret, line);
        }
    }

    // ---- CheckAsync ----

    private const string ModelBody = """{"id":"claude-opus-5-5","type":"model"}""";

    [Fact]
    public async Task Check_Ready()
    {
        _fake.Respond(200, ModelBody);
        using var m = Model();
        var s = await m.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new ModelStatus(true, "✓ API key works (claude-opus-5-5)"), s);
        var rec = _fake.Last;
        Assert.Equal(HttpMethod.Get, rec.Method);
        Assert.Equal("https://api.anthropic.com/v1/models/claude-opus-5-5", rec.Uri.ToString());
        Assert.Equal("", rec.Body);
        Assert.Equal("Bearer " + Key1, rec.Headers["Authorization"]);
        Assert.False(rec.Headers.ContainsKey("x-api-key"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Check_NoKey(bool throws)
    {
        _key = () => throws ? throw new IOException("x") : null;
        using var m = Model();
        Assert.Equal(new ModelStatus(false, "✗ No API key saved."), await m.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, _fake.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Check_Rejected(int status)
    {
        _fake.Respond(status, "{}");
        using var m = Model();
        Assert.Equal(new ModelStatus(false, $"✗ The API key was rejected (HTTP {status})."),
            await m.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    public async Task Check_ModelUnavailable(int status)
    {
        _fake.Respond(status, "{}");
        using var m = Model();
        Assert.Equal(new ModelStatus(false, "✗ The model \"claude-opus-5-5\" isn't available with this key."),
            await m.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(529)]
    [InlineData(429)]
    [InlineData(0)]
    public async Task Check_NoAnswer(int status)
    {
        if (status == 0) _fake.Throw();
        else _fake.Respond(status, "{}");
        using var m = Model();
        Assert.Equal(new ModelStatus(false, "✗ api.anthropic.com didn't answer."), await m.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _fake.Calls);
    }

    [Fact]
    public async Task Check_Timeout_IsNoAnswer()
    {
        _fake.Hang();
        using var m = Model();
        var t = m.CheckAsync(TestContext.Current.CancellationToken);
        await _fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(1));
        Assert.Equal(AnthropicApiStatusTexts.NoAnswer, (await t.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task Check_Cancel_Throws()
    {
        _fake.Hang();
        using var m = Model();
        using var cts = new CancellationTokenSource();
        var t = m.CheckAsync(cts.Token);
        await _fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Check_UsesSettingsModel()
    {
        _fake.Respond(200, ModelBody);
        _settings.Model = "claude-sonnet-5";
        using var m = Model();
        var s = await m.CheckAsync(TestContext.Current.CancellationToken);
        Assert.EndsWith("/v1/models/claude-sonnet-5", _fake.Last.Uri.ToString());
        Assert.Equal("✓ API key works (claude-sonnet-5)", s.Text);
    }

    [Fact]
    public async Task Check_InvalidModel_NoCall()
    {
        _settings.Model = "--bare";
        using var m = Model();
        var s = await m.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new ModelStatus(false, AnthropicApiStatusTexts.ModelUnavailable("--bare")), s);
        Assert.Equal(0, _fake.Calls);
        Assert.Equal(0, _keyCalls);
    }

    [Fact]
    public async Task Check_TextsNeverContainKey()
    {
        using var m = Model();
        foreach (var status in new[] { 200, 401, 403, 404, 400, 500 })
        {
            _fake.Respond(status, ModelBody);
            Assert.DoesNotContain("test-api-key", (await m.CheckAsync(TestContext.Current.CancellationToken)).Text);
        }
        _key = () => null;
        Assert.DoesNotContain("test-api-key", (await m.CheckAsync(TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public void StatusTexts_MatchSpecByteForByte()
    {
        Assert.Equal("✗ No API key saved.", AnthropicApiStatusTexts.NoKey);
        Assert.Equal("✗ api.anthropic.com didn't answer.", AnthropicApiStatusTexts.NoAnswer);
        Assert.Equal("✓ API key works (m)", AnthropicApiStatusTexts.Ready("m"));
        Assert.Equal("✗ The API key was rejected (HTTP 401).", AnthropicApiStatusTexts.Rejected(401));
        Assert.Equal("✗ The model \"m\" isn't available with this key.", AnthropicApiStatusTexts.ModelUnavailable("m"));
    }
}
