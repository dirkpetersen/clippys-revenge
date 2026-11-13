namespace ClippysRevenge.Core.Tests;

/// <summary>The adapter between M11.6's StyleSkill and M11.7's IStyleGuideSource seam.</summary>
public sealed class StyleGuideSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-sgs-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly StyleSkillFiles _files;
    private readonly FakeDraftModel _model = new();
    private readonly StyleSkill _skill;

    public StyleGuideSourceTests()
    {
        Directory.CreateDirectory(_root);
        _ix = new EmailIndex(Path.Combine(_root, "index.db"));
        _files = new StyleSkillFiles(StylePaths.Default(Path.Combine(_root, "home"), Path.Combine(_root, "appdata")));
        var settings = new AiDraftingSettings();
        _skill = new StyleSkill(_files, () => _ix, () => _model, () => settings, () => [], new ActivityHub());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static Func<CancellationToken, Task<Recipient?>> Owner(Recipient? r) => _ => Task.FromResult(r);

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new StyleGuideSource(null!, Owner(null)));
        Assert.Throws<ArgumentNullException>(() => new StyleGuideSource(_skill, null!));
    }

    [Fact]
    public void ReadForPrompt_NoSkillFiles_ReturnsTheFallbackText()
    {
        var source = new StyleGuideSource(_skill, Owner(null));
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
        var source = new StyleGuideSource(_skill, Owner(owner));
        await source.BeforePassAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task BeforePass_ValidOwner_DisabledSettings_NoModelCall()
    {
        var source = new StyleGuideSource(_skill, Owner(new Recipient("A User", "user@example.org")));
        await source.BeforePassAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task BeforePass_OwnerResolverFails_IsSwallowed()
    {
        var source = new StyleGuideSource(_skill, _ => throw new InvalidOperationException("Outlook went away"));
        await source.BeforePassAsync(TestContext.Current.CancellationToken);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task BeforePass_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new StyleGuideSource(_skill, ct => Task.FromCanceled<Recipient?>(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.BeforePassAsync(cts.Token));
    }
}
