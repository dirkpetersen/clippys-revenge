using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class ClaudeCliModelTests
{
    private const string HelpOk =
        "--json-schema <s> --no-session-persistence --strict-mcp-config --tools <t...> --effort <l> --system-prompt <p> --safe-mode";
    private const string AuthOk =
        """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"someone@example.org","orgId":"o-1","orgName":"Contoso","subscriptionType":"max"}""";
    private const string Envelope =
        """{"type":"result","subtype":"success","is_error":false,"result":"","usage":{"input_tokens":120,"output_tokens":45},"structured_output":{"needs_reply":true,"confidence":0.8,"reason":"x","body_html":"<p>Hi</p>"}}""";
    private const string ReadyText = "✓ Claude Code 2.1.323 · signed in (claude.ai, max)";

    private static CliRunResult Ex(string stdout, int code = 0, string stderr = "") =>
        new(CliRunOutcome.Exited, code, stdout, stderr, TimeSpan.FromMilliseconds(5));

    private static CliRunResult Out(CliRunOutcome o) => new(o, null, "", "", TimeSpan.Zero);

    private sealed class FakeRunner : IClaudeCliRunner
    {
        private readonly object _lock = new();
        public List<string> Calls { get; } = [];
        public CliRunResult Version = Ex("2.1.323 (Claude Code)\n");
        public CliRunResult Help = Ex(HelpOk);
        public CliRunResult Auth = Ex(AuthOk);
        public CliRunResult Prompt = Ex(Envelope);
        public int PromptCalls;
        public bool? SafeMode;

        public Task<CliRunResult> RunAsync(ClaudeCliCommand command, IReadOnlyList<string> args, string? stdin,
            TimeSpan timeout, CancellationToken ct)
        {
            lock (_lock) Calls.Add(args[0]);
            return Task.FromResult(args[0] switch { "--version" => Version, "--help" => Help, _ => Auth });
        }

        public Task<CliRunResult> RunPromptAsync(ClaudeCliCommand command, ModelRequest request, bool safeMode, CancellationToken ct)
        {
            lock (_lock) { PromptCalls++; SafeMode = safeMode; }
            return Task.FromResult(Prompt);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    private readonly FakeRunner _runner = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AiDraftingSettings _settings = new();
    private readonly CapturingLogger _log = new();
    private string? _dir = "/x";

    private ClaudeCliModel Model() =>
        new(() => _settings,
            new ClaudeCliLocator(k => k == "PATH" ? _dir : null, p => _dir is not null && p == _dir + "/claude", false),
            _runner, _time, _log);

    private static ModelRequest Req(string model = "claude-opus-5-5", string effort = "low", string schema = DraftModel.ReplySchema,
        TimeSpan? timeout = null) =>
        new("SYSPROMPT-XYZ", "USERPROMPT-XYZ", schema, model, effort, timeout ?? TimeSpan.FromSeconds(30), 8000);

    [Fact]
    public async Task NotFound_NoRunnerCalls()
    {
        _dir = null;
        var s = await Model().CheckAsync(CancellationToken.None);
        Assert.Equal(new ModelStatus(false, ClaudeCliStatusTexts.NotFound), s);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task VersionNotStarted_NotFound()
    {
        _runner.Version = Out(CliRunOutcome.NotStarted);
        Assert.Equal(new ModelStatus(false, ClaudeCliStatusTexts.NotFound), await Model().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Ready_TextHasNoPrivateFields()
    {
        var s = await Model().CheckAsync(CancellationToken.None);
        Assert.Equal(new ModelStatus(true, ReadyText), s);
        Assert.DoesNotContain("someone@", s.Text);
        Assert.DoesNotContain("Contoso", s.Text);
        Assert.DoesNotContain("o-1", s.Text);
    }

    [Fact]
    public async Task NoSafeMode_AddsHint()
    {
        _runner.Help = Ex(HelpOk.Replace("--safe-mode", ""));
        var s = await Model().CheckAsync(CancellationToken.None);
        Assert.EndsWith(" · update Claude Code for best results", s.Text);
        Assert.True(s.Ready);
    }

    [Fact]
    public async Task MissingRequiredFlag_TooOld()
    {
        _runner.Help = Ex(HelpOk.Replace("--json-schema", ""));
        Assert.Equal(new ModelStatus(false, "✗ Claude Code 2.1.323 is too old for AI drafts. Run \"claude update\", then press Check."),
            await Model().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SignedOut_NotSignedInText()
    {
        _runner.Auth = Ex("""{"loggedIn":false,"authMethod":"none"}""", 1);
        Assert.Equal(new ModelStatus(false, ClaudeCliStatusTexts.NotSignedIn), await Model().CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoAnswer_PerStep()
    {
        _runner.Version = Out(CliRunOutcome.TimedOut);
        Assert.Equal(ClaudeCliStatusTexts.NoAnswer("version"), (await Model().CheckAsync(CancellationToken.None)).Text);
        _runner.Version = Ex("2.1.323");
        _runner.Help = Out(CliRunOutcome.TimedOut);
        Assert.Equal(ClaudeCliStatusTexts.NoAnswer("help"), (await Model().CheckAsync(CancellationToken.None)).Text);
        _runner.Help = Ex(HelpOk);
        _runner.Auth = Ex("oops");
        Assert.Equal(ClaudeCliStatusTexts.NoAnswer("auth status"), (await Model().CheckAsync(CancellationToken.None)).Text);
        Assert.Equal("✗ Claude Code didn't answer (version). Press Check to try again.", ClaudeCliStatusTexts.NoAnswer("version"));
    }

    [Fact]
    public async Task VersionWithoutNumber_NoAnswer()
    {
        _runner.Version = Ex("unknown");
        Assert.Equal(ClaudeCliStatusTexts.NoAnswer("version"), (await Model().CheckAsync(CancellationToken.None)).Text);
    }

    [Fact]
    public async Task AuthFields_CleanedAndCut()
    {
        _runner.Auth = Ex("{\"loggedIn\":true,\"authMethod\":\"a\\u0007b" + new string('x', 60) + "\"}");
        var s = await Model().CheckAsync(CancellationToken.None);
        var expected = ("ab" + new string('x', 60))[..40];
        Assert.Equal(ClaudeCliStatusTexts.Ready("2.1.323", expected, null, true), s.Text);
        Assert.DoesNotContain('\u0007', s.Text);
    }

    [Fact]
    public async Task NoAuthMethod_NoParentheses()
    {
        _runner.Auth = Ex("""{"loggedIn":true,"subscriptionType":"max"}""");
        Assert.Equal("✓ Claude Code 2.1.323 · signed in", (await Model().CheckAsync(CancellationToken.None)).Text);
    }

    [Fact]
    public async Task Cache_ReadyIsCachedTenMinutes()
    {
        var m = Model();
        await m.CheckAsync(CancellationToken.None);
        await m.CheckAsync(CancellationToken.None);
        Assert.Equal(3, _runner.Calls.Count);
        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        await m.CheckAsync(CancellationToken.None);
        Assert.Equal(6, _runner.Calls.Count);
    }

    [Fact]
    public async Task Cache_InvalidateProbesAgain()
    {
        var m = Model();
        await m.CheckAsync(CancellationToken.None);
        m.InvalidateStatus();
        await m.CheckAsync(CancellationToken.None);
        Assert.Equal(6, _runner.Calls.Count);
    }

    [Fact]
    public async Task Cache_NotReadyNotCached()
    {
        _runner.Auth = Ex("""{"loggedIn":false}""", 1);
        var m = Model();
        await m.CheckAsync(CancellationToken.None);
        await m.CheckAsync(CancellationToken.None);
        Assert.Equal(6, _runner.Calls.Count);
    }

    [Fact]
    public async Task Cache_KeyChangesWithCommand()
    {
        var m = Model();
        await m.CheckAsync(CancellationToken.None);
        _dir = "/y";
        await m.CheckAsync(CancellationToken.None);
        Assert.Equal(6, _runner.Calls.Count);
    }

    [Fact]
    public async Task Check_Cancelled_Throws()
    {
        _runner.Version = Out(CliRunOutcome.Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Model().CheckAsync(new CancellationToken(true)));
    }

    [Fact]
    public async Task Run_Ok_PassesSafeMode()
    {
        var r = await Model().RunAsync(Req(), CancellationToken.None);
        Assert.IsType<ModelResult.Ok>(r);
        Assert.True(_runner.SafeMode);
    }

    [Fact]
    public async Task Run_NoSafeMode_PassesFalse()
    {
        _runner.Help = Ex(HelpOk.Replace("--safe-mode", ""));
        await Model().RunAsync(Req(), CancellationToken.None);
        Assert.False(_runner.SafeMode);
    }

    [Fact]
    public async Task Run_BadInput_NoRunnerCalls()
    {
        var m = Model();
        var bad = Failed(ModelFailure.BadInput);
        Assert.Equal(bad, await m.RunAsync(Req(model: "--bare"), CancellationToken.None));
        Assert.Equal(bad, await m.RunAsync(Req(effort: "ultra"), CancellationToken.None));
        Assert.Equal(bad, await m.RunAsync(Req(schema: "[]"), CancellationToken.None));
        Assert.Equal(bad, await m.RunAsync(Req(timeout: TimeSpan.Zero), CancellationToken.None));
        Assert.Empty(_runner.Calls);
        Assert.Equal(0, _runner.PromptCalls);
    }

    private static ModelResult Failed(ModelFailure k, int? code = null) => new ModelResult.Failed(k, code);

    [Fact]
    public async Task Run_ProbeNotReady_Mapped()
    {
        _dir = null;
        Assert.Equal(Failed(ModelFailure.NotInstalled), await Model().RunAsync(Req(), CancellationToken.None));

        _dir = "/x";
        _runner.Help = Ex(HelpOk.Replace("--tools", ""));
        Assert.Equal(Failed(ModelFailure.NotInstalled), await Model().RunAsync(Req(), CancellationToken.None));

        _runner.Help = Ex(HelpOk);
        _runner.Auth = Ex("""{"loggedIn":false}""", 1);
        Assert.IsType<ModelResult.NotSignedIn>(await Model().RunAsync(Req(), CancellationToken.None));

        _runner.Auth = Ex("oops");
        Assert.Equal(Failed(ModelFailure.ProcessExit), await Model().RunAsync(Req(), CancellationToken.None));
        Assert.Equal(0, _runner.PromptCalls);
    }

    [Theory]
    [InlineData(CliRunOutcome.TimedOut, ModelFailure.Timeout)]
    [InlineData(CliRunOutcome.Cancelled, ModelFailure.Cancelled)]
    [InlineData(CliRunOutcome.OutputTooLarge, ModelFailure.BadOutput)]
    [InlineData(CliRunOutcome.PromptTooLarge, ModelFailure.BadInput)]
    [InlineData(CliRunOutcome.NotStarted, ModelFailure.NotInstalled)]
    [InlineData(CliRunOutcome.SetupFailed, ModelFailure.ProcessExit)]
    public async Task Run_OutcomeMapping(CliRunOutcome o, ModelFailure f)
    {
        _runner.Prompt = Out(o);
        Assert.Equal(Failed(f), await Model().RunAsync(Req(), CancellationToken.None));
    }

    [Fact]
    public async Task Run_NotSignedIn_InvalidatesStatus()
    {
        var m = Model();
        _runner.Prompt = Ex("Not logged in · Please run /login", 1);
        Assert.IsType<ModelResult.NotSignedIn>(await m.RunAsync(Req(), CancellationToken.None));
        var before = _runner.Calls.Count;
        await m.CheckAsync(CancellationToken.None);
        Assert.Equal(before + 3, _runner.Calls.Count);
    }

    [Fact]
    public async Task Concurrency_OneProbe()
    {
        var m = Model();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => m.CheckAsync(CancellationToken.None)));
        Assert.Equal(3, _runner.Calls.Count);
    }

    [Fact]
    public async Task Logging_HasNoPrivateData()
    {
        var m = Model();
        await m.CheckAsync(CancellationToken.None);
        await m.RunAsync(Req(), CancellationToken.None);
        Assert.NotEmpty(_log.Lines);
        foreach (var line in _log.Lines)
            foreach (var bad in new[] { "someone@", "Contoso", "claude.ai", "max", "SYSPROMPT-XYZ", "USERPROMPT-XYZ", "<p>Hi</p>" })
                Assert.DoesNotContain(bad, line);
    }
}
