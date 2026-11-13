namespace ClippysRevenge.Core.Tests;

public class DraftModelSwitchTests
{
    private sealed class Tagged(string tag) : IDraftModel
    {
        public Task<ModelStatus> CheckAsync(CancellationToken ct) => Task.FromResult(new ModelStatus(true, tag));

        public Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct) =>
            Task.FromResult<ModelResult>(new ModelResult.Failed(ModelFailure.BadInput, tag == "cli" ? 1 : 2));
    }

    private static readonly ModelRequest Req = new("s", "u", "{}", "m", "low", TimeSpan.FromSeconds(1), 1);

    [Fact]
    public async Task Backend_is_read_on_every_call()
    {
        var s = new AiDraftingSettings();
        var sw = new DraftModelSwitch(() => s, new Tagged("cli"), new Tagged("api"));
        Assert.Equal("cli", (await sw.CheckAsync(CancellationToken.None)).Text);
        s.Backend = AiBackend.ApiKey;
        Assert.Equal("api", (await sw.CheckAsync(CancellationToken.None)).Text);
        Assert.Equal(2, ((ModelResult.Failed)await sw.RunAsync(Req, CancellationToken.None)).Code);
        s.Backend = AiBackend.ClaudeCode;
        Assert.Equal(1, ((ModelResult.Failed)await sw.RunAsync(Req, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Missing_api_backend()
    {
        var sw = new DraftModelSwitch(() => new AiDraftingSettings { Backend = AiBackend.ApiKey }, new Tagged("cli"), null);
        Assert.Equal(new ModelStatus(false, DraftModelSwitch.ApiKeyUnavailableText), await sw.CheckAsync(CancellationToken.None));
        Assert.Equal(new ModelResult.Failed(ModelFailure.NotInstalled, null), await sw.RunAsync(Req, CancellationToken.None));
    }

    [Fact]
    public void Argument_checks()
    {
        Assert.Throws<ArgumentNullException>(() => new DraftModelSwitch(null!, new Tagged("cli"), null));
        Assert.Throws<ArgumentNullException>(() => new DraftModelSwitch(() => new AiDraftingSettings(), null!, null));
        var sw = new DraftModelSwitch(() => new AiDraftingSettings(), new Tagged("cli"), null);
        Assert.Throws<ArgumentNullException>(() => { _ = sw.RunAsync(null!, CancellationToken.None); });
    }
}
