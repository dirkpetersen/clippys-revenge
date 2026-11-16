using System.Text;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public sealed class FactsReporterTests : IDisposable
{
    private const string Marker = "FACTS-MARKER-42";

    private sealed class ThrowingLog : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("log");
    }

    private sealed class LevelLog : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-factsr-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly LevelLog _log = new();

    public FactsReporterTests() => _hub = new ActivityHub(_time);

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private FactsPaths Paths => FactsPaths.Default(Path.Combine(_root, "profile"), Path.Combine(_root, "appdata"));

    private FactsFile File_() => new(Paths, _time);

    private void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.File)!);
        File.WriteAllText(Paths.File, text, new UTF8Encoding(false));
    }

    private string[] AiEvents() => _hub.GetEvents().Where(e => e.Kind == ActivityKind.Ai).Select(e => e.Summary).ToArray();

    private FactsReporter Reporter() => new(File_(), _hub, _log);

    // 21
    [Fact]
    public void Constructor_validates()
    {
        Assert.Throws<ArgumentNullException>(() => new FactsReporter(null!, _hub));
        Assert.Throws<ArgumentNullException>(() => new FactsReporter(File_(), null!));
    }

    // 22
    [Fact]
    public void File_with_content_is_silent()
    {
        Write("Zoom: x");
        var r = Reporter();
        for (int i = 0; i < 3; i++) Assert.Equal("Zoom: x", r.ReadForPrompt());
        Assert.Empty(AiEvents());
        Assert.Empty(_log.Lines);
    }

    // 23
    [Fact]
    public void Untouched_template_is_silent()
    {
        Write(FactsTemplate.Render("Ann"));
        Assert.Equal("", Reporter().ReadForPrompt());
        Assert.Empty(AiEvents());
        Assert.Empty(_log.Lines);
    }

    // 24
    [Fact]
    public void Missing_file_reports_once()
    {
        var r = Reporter();
        Assert.Equal("", r.ReadForPrompt());
        Assert.Equal("", r.ReadForPrompt());
        Assert.Equal([FactsTexts.SourceNone], AiEvents());
        Assert.Equal([(LogLevel.Warning, FactsTexts.SourceNone)], _log.Lines);
    }

    // 25
    [Fact]
    public void Transitions()
    {
        Write("A");
        var r = Reporter();
        Assert.Equal("A", r.ReadForPrompt());
        File.Delete(Paths.File);
        r.ReadForPrompt();
        r.ReadForPrompt();
        Assert.Equal([FactsTexts.SourceNone], AiEvents());
        Write(FactsTemplate.Render("Ann"));
        r.ReadForPrompt();
        Assert.Equal([FactsTexts.SourceNone, FactsTexts.SourceBack], AiEvents());
        Assert.Equal((LogLevel.Information, FactsTexts.SourceBack), _log.Lines[^1]);
        Write("filled");
        Assert.Equal("filled", r.ReadForPrompt());
        Write(FactsTemplate.Render("Ann"));
        Assert.Equal("", r.ReadForPrompt());
        Assert.Equal(2, AiEvents().Length);
        Assert.Equal(2, _log.Lines.Count);
    }

    // 26
    [Fact]
    public void Empty_then_deleted_reports_none()
    {
        Write(FactsTemplate.Render("Ann"));
        var r = Reporter();
        r.ReadForPrompt();
        File.Delete(Paths.File);
        r.ReadForPrompt();
        Assert.Equal([FactsTexts.SourceNone], AiEvents());
    }

    // 27
    [Fact]
    public void Texts_hold_no_path_or_content()
    {
        Assert.Equal("Facts about you: my-email-context SKILL.md not found or unreadable — drafts go without it. Use \"Edit my facts…\" in the AI drafts menu.",
            FactsTexts.SourceNone);
        Assert.Equal("Facts about you: using my-email-context SKILL.md again.", FactsTexts.SourceBack);
        Write(Marker);
        var r = Reporter();
        r.ReadForPrompt();
        File.Delete(Paths.File);
        r.ReadForPrompt();
        Write(Marker);
        r.ReadForPrompt();
        var all = AiEvents().Concat(_log.Lines.Select(l => l.Text)).ToArray();
        Assert.NotEmpty(all);
        Assert.All(all, t =>
        {
            Assert.DoesNotContain(Marker, t);
            Assert.DoesNotContain(_root, t);
        });
    }

    // 29
    [Fact]
    public void Throwing_hub_and_logger_do_not_stop_reading()
    {
        Write("body");
        _hub.Dispose();
        var r = new FactsReporter(File_(), _hub, new ThrowingLog());
        Assert.Equal("body", r.ReadForPrompt());
        File.Delete(Paths.File);
        Assert.Equal("", r.ReadForPrompt());
        Write("body");
        Assert.Equal("body", r.ReadForPrompt());
    }

    // ---- starter

    private static AiDraftingSettings Consent(bool consent)
    {
        var s = new AiDraftingSettings();
        if (consent) s.RecordConsent(DateTimeOffset.Parse("2026-11-13T00:00:00Z"));
        return s;
    }

    private FactsTemplateStarter Starter(Func<AiDraftingSettings> settings, Func<CancellationToken, Task<string?>> name) =>
        new(File_(), settings, name, _log);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void AssertLogClean()
    {
        lock (_log.Lines)
            Assert.All(_log.Lines, l =>
            {
                Assert.DoesNotContain("Ann", l.Text);
                Assert.DoesNotContain(_root, l.Text);
                Assert.DoesNotContain("SKILL.md", l.Text);
            });
    }

    // 30
    [Fact]
    public async Task No_consent()
    {
        var calls = 0;
        var s = Starter(() => Consent(false), _ => { calls++; return Task.FromResult<string?>("Ann"); });
        Assert.Equal(FactsTemplateResult.NoConsent, await s.RunAsync(Ct));
        Assert.Equal(0, calls);
        Assert.False(File.Exists(Paths.File));
        AssertLogClean();
    }

    // 31
    [Fact]
    public async Task Creates_once()
    {
        var calls = 0;
        var s = Starter(() => Consent(true), _ => { calls++; return Task.FromResult<string?>("Ann"); });
        Assert.Equal(FactsTemplateResult.Created, await s.RunAsync(Ct));
        Assert.Equal(FactsTemplate.Render("Ann"), File.ReadAllText(Paths.File));
        Assert.True(File.Exists(Paths.StateFile));
        Assert.Contains((LogLevel.Information, "Facts about you: template created."), _log.Lines);
        Assert.Equal(FactsTemplateResult.AlreadyDone, await s.RunAsync(Ct));
        Assert.Equal(1, calls);
        AssertLogClean();
    }

    // 32
    [Fact]
    public async Task Waits_for_the_name()
    {
        string? name = null;
        var s = Starter(() => Consent(true), _ => Task.FromResult(name));
        Assert.Equal(FactsTemplateResult.NameUnknown, await s.RunAsync(Ct));
        Assert.False(File.Exists(Paths.File));
        name = "Ann";
        Assert.Equal(FactsTemplateResult.Created, await s.RunAsync(Ct));
        AssertLogClean();
    }

    // 33
    [Fact]
    public async Task Throwing_name_lookup_is_unknown()
    {
        var s = Starter(() => Consent(true), _ => throw new InvalidOperationException("x"));
        Assert.Equal(FactsTemplateResult.NameUnknown, await s.RunAsync(Ct));
        Assert.False(File.Exists(Paths.File));
    }

    // 34
    [Fact]
    public async Task Cancelled_token_throws()
    {
        var s = Starter(() => Consent(true), _ => Task.FromResult<string?>("Ann"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.RunAsync(cts.Token));
    }

    // 35
    [Fact]
    public async Task State_file_present_means_done()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.StateFile)!);
        File.WriteAllText(Paths.StateFile, "{}");
        var calls = 0;
        var s = Starter(() => Consent(true), _ => { calls++; return Task.FromResult<string?>("Ann"); });
        Assert.Equal(FactsTemplateResult.AlreadyDone, await s.RunAsync(Ct));
        Assert.False(File.Exists(Paths.File));
        Assert.Equal(0, calls);
    }

    // 36
    [Fact]
    public async Task Existing_user_file_untouched()
    {
        Write("mine");
        var s = Starter(() => Consent(true), _ => Task.FromResult<string?>("Ann"));
        Assert.Equal(FactsTemplateResult.AlreadyExists, await s.RunAsync(Ct));
        Assert.Equal("mine", File.ReadAllText(Paths.File));
        Assert.True(File.Exists(Paths.StateFile));
    }

    // 37
    [Fact]
    public async Task Failure_warns_and_retries()
    {
        var dir = Path.GetDirectoryName(Paths.File)!;
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        File.WriteAllText(dir, "in the way");
        var s = Starter(() => Consent(true), _ => Task.FromResult<string?>("Ann"));
        Assert.Equal(FactsTemplateResult.Failed, await s.RunAsync(Ct));
        var w = Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning);
        Assert.StartsWith("Facts about you: couldn't create the template (", w.Text);
        Assert.Equal(FactsTemplateResult.Failed, await s.RunAsync(Ct));
        AssertLogClean();
    }

    // 38
    [Fact]
    public async Task Single_flight()
    {
        var gate = new TaskCompletionSource<string?>();
        var s = Starter(() => Consent(true), _ => gate.Task);
        var first = s.RunAsync(Ct);
        Assert.Equal(FactsTemplateResult.Busy, await s.RunAsync(Ct));
        gate.SetResult("Ann");
        Assert.Equal(FactsTemplateResult.Created, await first);
    }

    // 39
    [Fact]
    public async Task Kick_never_throws_and_creates()
    {
        new FactsTemplateStarter(File_(), () => throw new InvalidOperationException(), _ => Task.FromResult<string?>("Ann")).Kick();
        var s = Starter(() => Consent(true), _ => Task.FromResult<string?>("Ann"));
        s.Kick();
        for (int i = 0; i < 100 && !File.Exists(Paths.StateFile); i++) await Task.Delay(50, Ct);
        Assert.True(File.Exists(Paths.File));
        s.Kick();
        AssertLogClean();
    }
}
