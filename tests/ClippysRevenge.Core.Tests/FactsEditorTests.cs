namespace ClippysRevenge.Core.Tests;

public sealed class FactsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-factsed-" + Guid.NewGuid().ToString("N"));

    private FactsPaths Paths => FactsPaths.Default(Path.Combine(_root, "profile"), Path.Combine(_root, "appdata"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Missing_file_is_created_from_template_with_owner_name()
    {
        var ed = new FactsEditor(new FactsFile(Paths), _ => Task.FromResult<string?>("Ada Lovelace"));
        var t = await ed.PrepareAsync(CancellationToken.None);
        Assert.Equal(Paths.File, t.Path);
        Assert.Null(t.Error);
        Assert.Contains("Facts about Ada Lovelace", File.ReadAllText(Paths.File));
        Assert.True(File.Exists(Paths.StateFile));
    }

    [Fact]
    public async Task Unknown_or_failing_name_still_creates_the_template()
    {
        var ed = new FactsEditor(new FactsFile(Paths), _ => throw new InvalidOperationException("outlook down"));
        var t = await ed.PrepareAsync(CancellationToken.None);
        Assert.Equal(Paths.File, t.Path);
        Assert.Contains("Facts about the user", File.ReadAllText(Paths.File));
    }

    [Fact]
    public async Task No_name_source_creates_the_template()
    {
        var t = await new FactsEditor(new FactsFile(Paths)).PrepareAsync(CancellationToken.None);
        Assert.Equal(Paths.File, t.Path);
        Assert.True(File.Exists(Paths.File));
    }

    [Fact]
    public async Task Existing_file_is_untouched_and_the_name_is_not_asked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.File)!);
        File.WriteAllText(Paths.File, "mine");
        var asked = false;
        var ed = new FactsEditor(new FactsFile(Paths), _ => { asked = true; return Task.FromResult<string?>("x"); });
        var t = await ed.PrepareAsync(CancellationToken.None);
        Assert.Equal(Paths.File, t.Path);
        Assert.False(asked);
        Assert.Equal("mine", File.ReadAllText(Paths.File));
    }

    [Fact]
    public async Task Deleted_file_is_recreated_even_though_the_state_file_exists()
    {
        var ed = new FactsEditor(new FactsFile(Paths));
        await ed.PrepareAsync(CancellationToken.None);
        File.Delete(Paths.File);
        var t = await ed.PrepareAsync(CancellationToken.None);
        Assert.Equal(Paths.File, t.Path);
        Assert.True(File.Exists(Paths.File));
    }

    [Fact]
    public async Task Create_failure_gives_an_error_with_the_exception_type_only()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(Paths.File))!);
        // A file where the skill folder should be makes CreateDirectory fail.
        File.WriteAllText(Path.GetDirectoryName(Paths.File)!, "x");
        var t = await new FactsEditor(new FactsFile(Paths)).PrepareAsync(CancellationToken.None);
        Assert.Null(t.Path);
        Assert.StartsWith("couldn't create the file (", t.Error);
        Assert.DoesNotContain(_root, t.Error);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FactsEditor(new FactsFile(Paths)).PrepareAsync(cts.Token));
    }

    [Fact]
    public void Status_text_follows_the_file()
    {
        var ed = new FactsEditor(new FactsFile(Paths));
        Assert.Equal("Facts about you: not found", ed.StatusText());
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.File)!);
        File.WriteAllText(Paths.File, FactsTemplate.Render("Ada"));
        Assert.Equal("Facts about you: template only", ed.StatusText());
        File.AppendAllText(Paths.File, "\nI lead the team.\n");
        Assert.Equal("Facts about you: filled in", ed.StatusText());
    }

    [Fact]
    public void Texts_and_menu_flag()
    {
        Assert.Equal("Edit my facts…", AiMenuTexts.EditFacts);
        Assert.Equal("Include related earlier emails", AiTabTexts.RelatedMailText);
        Assert.Equal("Start each draft with a highlighted note on why it was drafted", AiTabTexts.ReasonLineText);
        var live = new AiDraftingSettings();
        Assert.True(AiDraftsMenu.Build(live, true, true, true).EditFactsEnabled);
        Assert.False(AiDraftsMenu.Build(live, false, false, false).EditFactsEnabled);
    }
}
