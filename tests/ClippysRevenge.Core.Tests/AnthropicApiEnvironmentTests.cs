namespace ClippysRevenge.Core.Tests;

[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("ProcessEnvironment")]
public sealed class AnthropicApiEnvironmentTests : IDisposable
{
    private static readonly string[] Vars =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE"];

    private readonly Dictionary<string, string?> _saved = [];

    public AnthropicApiEnvironmentTests()
    {
        foreach (var v in Vars) _saved[v] = Environment.GetEnvironmentVariable(v);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-key-x");
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "env-tok-x");
        Environment.SetEnvironmentVariable("ANTHROPIC_BASE_URL", "https://example.invalid");
        Environment.SetEnvironmentVariable("ANTHROPIC_PROFILE", "p");
    }

    public void Dispose()
    {
        foreach (var (k, v) in _saved) Environment.SetEnvironmentVariable(k, v);
    }

    private static ModelRequest Req() =>
        new("SYS", "USER", DraftModel.ReplySchema, "claude-opus-5-5", "medium", TimeSpan.FromSeconds(180), 8000);

    [Fact]
    public async Task EnvironmentIsIgnored_WhenKeySaved()
    {
        var fake = new FakeAnthropicHandler();
        using var m = new AnthropicApiModel(() => new AiDraftingSettings(), () => "test-api-key-0001", fake);
        Assert.IsType<ModelResult.Ok>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        fake.Respond(200, """{"id":"claude-opus-5-5","type":"model"}""");
        Assert.True((await m.CheckAsync(TestContext.Current.CancellationToken)).Ready);

        Assert.Equal(2, fake.Calls);
        foreach (var r in fake.Requests)
        {
            Assert.StartsWith("https://api.anthropic.com/", r.Uri.ToString());
            Assert.Equal("Bearer test-api-key-0001", r.Headers["Authorization"]);
            Assert.All(r.Headers.Values, v => Assert.DoesNotContain("env-", v));
        }
    }

    [Fact]
    public async Task NoSavedKey_DoesNotFallBackToEnvironment()
    {
        var fake = new FakeAnthropicHandler();
        using var m = new AnthropicApiModel(() => new AiDraftingSettings(), () => null, fake);
        Assert.IsType<ModelResult.NotSignedIn>(await m.RunAsync(Req(), TestContext.Current.CancellationToken));
        Assert.Equal(AnthropicApiStatusTexts.NoKey, (await m.CheckAsync(TestContext.Current.CancellationToken)).Text);
        Assert.Equal(0, fake.Calls);
    }
}
