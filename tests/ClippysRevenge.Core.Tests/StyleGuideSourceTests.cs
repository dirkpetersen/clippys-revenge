using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

/// <summary>The adapter between M11.6's StyleSkill and M11.7's IStyleGuideSource seam.</summary>
public sealed class StyleGuideSourceTests : IDisposable
{
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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-sgs-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly StylePaths _paths;
    private readonly StyleSkillFiles _files;
    private readonly FakeDraftModel _model = new();
    private readonly StyleSkill _skill;
    private readonly ActivityHub _hub = new();
    private readonly CapturingLogger _log = new();

    public StyleGuideSourceTests()
    {
        Directory.CreateDirectory(_root);
        _ix = new EmailIndex(Path.Combine(_root, "index.db"));
        _paths = StylePaths.Default(Path.Combine(_root, "home"), Path.Combine(_root, "appdata"));
        _files = new StyleSkillFiles(_paths);
        var settings = new AiDraftingSettings();
        _skill = new StyleSkill(_files, () => _ix, () => _model, () => settings, () => [], new ActivityHub());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Func<CancellationToken, Task<Recipient?>> Owner(Recipient? r) => _ => Task.FromResult(r);

    private StyleGuideSource Source(Recipient? owner = null) => new(_skill, Owner(owner), _hub, _log);

    private static string FileText(string body) => StyleSkillText.Render("Dirk", new DateOnly(2026, 11, 13), 30, body);

    private static void Put(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private IReadOnlyList<ActivityEvent> AiEvents => [.. _hub.GetEvents().Where(e => e.Kind == ActivityKind.Ai)];

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new StyleGuideSource(null!, Owner(null), _hub));
        Assert.Throws<ArgumentNullException>(() => new StyleGuideSource(_skill, null!, _hub));
        Assert.Throws<ArgumentNullException>(() => new StyleGuideSource(_skill, Owner(null), null!));
    }

    [Fact]
    public void ReadForPrompt_NoSkillFiles_ReturnsTheFallbackText()
    {
        var source = Source();
        Assert.Equal(DraftPrompt.NoStyleGuideText, source.ReadForPrompt());
    }

    [Fact]
    public void FallbackTexts_AreTheSameInBothSeams()
    {
        Assert.Equal(DraftPrompt.NoStyleGuideText, StyleSkillText.NoStyleGuideLine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforePass_UnknownOrInvalidOwner_DoesNothing(bool invalidAddress)
    {
        var owner = invalidAddress ? new Recipient("Someone", "not-an-address") : null;
        var source = Source(owner);
        await source.BeforePassAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task BeforePass_ValidOwner_DisabledSettings_NoModelCall()
    {
        var source = Source(new Recipient("A User", "user@example.org"));
        await source.BeforePassAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task BeforePass_OwnerResolverFails_IsSwallowed()
    {
        var source = new StyleGuideSource(_skill, _ => throw new InvalidOperationException("Outlook went away"), _hub, _log);
        await source.BeforePassAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task BeforePass_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new StyleGuideSource(_skill, ct => Task.FromCanceled<Recipient?>(ct), _hub, _log);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.BeforePassAsync(cts.Token));
    }

    // ---- visible style source (SPEC 19.6) ----

    [Fact]
    public void UserFileFromTheStart_ReportsNothing()
    {
        Put(_paths.UserSkillFile, FileText("USER BODY"));
        var source = Source();
        for (int i = 0; i < 3; i++) Assert.Contains("USER BODY", source.ReadForPrompt());
        Assert.Empty(AiEvents);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void NoFiles_ReportsOnce()
    {
        var source = Source();
        source.ReadForPrompt();
        source.ReadForPrompt();
        var e = Assert.Single(AiEvents);
        Assert.Equal(StyleTexts.SourceNone, e.Summary);
        Assert.Equal([$"Warning|{StyleTexts.SourceNone}"], _log.Lines);
    }

    [Fact]
    public void UserThenAppCopyThenBack_ReportsEachChangeOnce()
    {
        Put(_paths.UserSkillFile, FileText("USER BODY"));
        Put(_paths.AppCopyFile, FileText("APP BODY"));
        var source = Source();
        Assert.Contains("USER BODY", source.ReadForPrompt());
        Assert.Empty(AiEvents);

        File.Delete(_paths.UserSkillFile);
        Assert.Contains("APP BODY", source.ReadForPrompt());
        Assert.Contains("APP BODY", source.ReadForPrompt());
        Assert.Equal([StyleTexts.SourceAppCopy], AiEvents.Select(e => e.Summary));
        Assert.Equal([$"Warning|{StyleTexts.SourceAppCopy}"], _log.Lines);

        Put(_paths.UserSkillFile, FileText("USER BODY"));
        Assert.Contains("USER BODY", source.ReadForPrompt());
        Assert.Contains("USER BODY", source.ReadForPrompt());
        Assert.Equal([StyleTexts.SourceAppCopy, StyleTexts.SourceBack], AiEvents.Select(e => e.Summary));
        Assert.Equal($"Information|{StyleTexts.SourceBack}", _log.Lines[^1]);
        Assert.Equal(2, _log.Lines.Count);
    }

    [Fact]
    public void AppCopyToNone_AndNoneToAppCopy()
    {
        Put(_paths.AppCopyFile, FileText("APP BODY"));
        var source = Source();
        source.ReadForPrompt();
        File.Delete(_paths.AppCopyFile);
        source.ReadForPrompt();
        Put(_paths.AppCopyFile, FileText("APP BODY"));
        source.ReadForPrompt();
        Assert.Equal([StyleTexts.SourceAppCopy, StyleTexts.SourceNone, StyleTexts.SourceAppCopy], AiEvents.Select(e => e.Summary));
    }

    [Fact]
    public void Texts_AreExact_AndCarryNoPathOrContent()
    {
        Assert.Equal("Writing style: SKILL.md not found or unreadable — drafts use no style guide. Regenerate it from AI drafts.",
            StyleTexts.SourceNone);
        Assert.Equal("Writing style: your SKILL.md is missing or unreadable, so drafts use Clippy's saved copy. Regenerate it from AI drafts, or fix the file.",
            StyleTexts.SourceAppCopy);
        Assert.Equal("Writing style: using your SKILL.md again.", StyleTexts.SourceBack);

        Put(_paths.AppCopyFile, FileText("SECRET CONTENT"));
        Source().ReadForPrompt();
        foreach (var line in _log.Lines.Concat(AiEvents.Select(e => e.Summary)))
        {
            Assert.DoesNotContain(_root, line);
            Assert.DoesNotContain("SECRET CONTENT", line);
        }
    }

    [Fact]
    public void ReadForPrompt_ReturnsTheSameBodyAsTheSkill_InEveryState()
    {
        var source = Source();
        Assert.Equal(_skill.ReadBodyForPrompt(), source.ReadForPrompt());
        Put(_paths.AppCopyFile, FileText("APP BODY"));
        Assert.Equal(_skill.ReadBodyForPrompt(), source.ReadForPrompt());
        Put(_paths.UserSkillFile, FileText("USER BODY"));
        Assert.Equal(_skill.ReadBodyForPrompt(), source.ReadForPrompt());
    }
}
