using System.Text;
using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public sealed class FactsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-facts-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    private FactsPaths Paths => FactsPaths.Default(Path.Combine(_root, "profile"), Path.Combine(_root, "appdata"));

    private FactsFile Make() => new(Paths, _time);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.File)!);
        File.WriteAllText(Paths.File, text, new UTF8Encoding(false));
    }

    private void WriteBytes(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.File)!);
        File.WriteAllBytes(Paths.File, bytes);
    }

    private static string Expected(string name) => string.Join("\n",
        "---",
        "name: my-email-context",
        "description: \"Facts about " + name + " for writing their email: role and team, meeting links, office hours, standard answers and who people are. Use when drafting or replying to email as " + name + ".\"",
        "---",
        "",
        "<!-- Facts about you, for Clippy's Revenge and Claude Code. Clippy adds what you write here to every AI draft request it sends to Claude, and Claude Code reads it when it writes email for you. Write short facts under the headings, one per line. Text between these comment markers is ignored, so you can leave the examples. Clippy never changes this file once it exists. Don't put passwords or other secrets here. -->",
        "",
        "## Me",
        "",
        "<!-- Your role, your team and what you work on. -->",
        "<!-- e.g. I lead the research computing team; we run the HPC cluster and research storage. -->",
        "",
        "## Links",
        "",
        "<!-- Links you often send. -->",
        "<!-- e.g. My Zoom room: https://zoom.us/my/example -->",
        "",
        "## Office hours",
        "",
        "<!-- When and how people can reach you. -->",
        "<!-- e.g. Office hours Tuesdays 2-4 pm, on Zoom or in my office. -->",
        "",
        "## Standard answers",
        "",
        "<!-- Answers you give again and again. -->",
        "<!-- e.g. New accounts: please fill in the request form at https://example.org/accounts -->",
        "",
        "## People",
        "",
        "<!-- Who people are and how you know them, one per line. -->",
        "<!-- e.g. Ricky, Dave: ski group -->",
        "");

    // 1
    [Fact]
    public void Paths_default_linux_style()
    {
        var root = Path.DirectorySeparatorChar == '/' ? "/home/ann" : @"C:\Users\ann";
        var app = Path.DirectorySeparatorChar == '/' ? "/home/ann/.config/ClippysRevenge" : @"C:\Users\ann\AppData\Roaming\ClippysRevenge";
        var p = FactsPaths.Default(root, app);
        Assert.Equal(Path.Combine(root, ".claude", "skills", "my-email-context", "SKILL.md"), p.File);
        Assert.Equal(Path.Combine(app, "my-email-style", "facts-state.json"), p.StateFile);
    }

    [Theory]
    [InlineData("", "/abs")]
    [InlineData("rel", "/abs")]
    [InlineData(null, "/abs")]
    public void Paths_default_rejects_bad_input(string? profile, string app)
    {
        var abs = Path.GetFullPath(app);
        Assert.Throws<ArgumentException>(() => FactsPaths.Default(profile!, abs));
        Assert.Throws<ArgumentException>(() => FactsPaths.Default(abs, profile!));
    }

    // 2
    [Fact]
    public void Constructor_validates()
    {
        Assert.Throws<ArgumentNullException>(() => new FactsFile(null!));
        Assert.Throws<ArgumentException>(() => new FactsFile(new FactsPaths("rel/SKILL.md", Paths.StateFile)));
    }

    // 3
    [Fact]
    public void Template_is_exact()
    {
        var t = FactsTemplate.Render("Ann Lee");
        Assert.Equal(Expected("Ann Lee"), t);
        Assert.StartsWith("---\nname: my-email-context\n", t);
        Assert.EndsWith("-->\n", t);
        Assert.Contains("<!-- e.g. Ricky, Dave: ski group -->", t);
    }

    // 4
    [Fact]
    public void Template_name_is_cleaned()
    {
        Assert.Contains("Facts about the user for", FactsTemplate.Render(null));
        Assert.Contains("Facts about the user for", FactsTemplate.Render("  "));
        var t = FactsTemplate.Render("A \"B\" \\C");
        Assert.Contains("Facts about A B C for", t);
        Assert.DoesNotContain("\\C", t);
    }

    // 5
    [Fact]
    public void Untouched_template_is_empty()
    {
        Assert.Equal("", FactsFile.Clean(FactsTemplate.Render("Ann")));
        Assert.Equal("", FactsFile.Clean(FactsTemplate.Render(null)));
        Write(FactsTemplate.Render("Ann"));
        Assert.Equal(new FactsPromptText("", FactsSource.Empty), Make().ReadForPrompt());
    }

    // 6
    [Fact]
    public void Link_under_links_heading()
    {
        var t = FactsTemplate.Render("Ann").Replace("<!-- e.g. My Zoom room: https://zoom.us/my/example -->\n",
            "<!-- e.g. My Zoom room: https://zoom.us/my/example -->\n- Zoom: https://zoom.us/j/1\n");
        Write(t);
        Assert.Equal(new FactsPromptText("## Links\n\n- Zoom: https://zoom.us/j/1", FactsSource.File), Make().ReadForPrompt());
    }

    // 7
    [Fact]
    public void Two_sections_filled()
    {
        var t = FactsTemplate.Render("Ann")
            .Replace("<!-- e.g. I lead the research computing team; we run the HPC cluster and research storage. -->\n",
                "<!-- e.g. I lead -->\nI run the HPC team.\n")
            .Replace("<!-- e.g. Ricky, Dave: ski group -->\n", "<!-- e.g. Ricky, Dave: ski group -->\nRicky, Dave: ski group\n");
        Write(t);
        Assert.Equal("## Me\n\nI run the HPC team.\n\n## People\n\nRicky, Dave: ski group", Make().ReadForPrompt().Body);
    }

    // 8
    [Theory]
    [InlineData("Hello", "Hello")]
    [InlineData("---\nname: x\n---\n", "")]
    [InlineData("a <!-- x --> b", "a  b")]
    [InlineData("a <!-- x\ny\nz --> b", "a  b")]
    [InlineData("a <!-- b", "a <!-- b")]
    [InlineData("a\r\nb\r\n", "a\nb")]
    [InlineData("a\n\n\n\nb", "a\n\nb")]
    [InlineData("## A\n### B\n", "")]
    [InlineData("## A\n### B\nx", "## A\n### B\nx")]
    [InlineData("#hashtag", "#hashtag")]
    [InlineData("x\u0007y", "xy")]
    [InlineData("a\tb", "a\tb")]
    [InlineData("a   \nb", "a\nb")]
    [InlineData(null, "")]
    public void Clean_cases(string? input, string expected) => Assert.Equal(expected, FactsFile.Clean(input));

    // 9
    [Fact]
    public void Size_limit()
    {
        WriteBytes(Encoding.ASCII.GetBytes(new string('a', 16384)));
        var r = Make().ReadForPrompt();
        Assert.Equal(FactsSource.File, r.Source);
        Assert.Equal(16384, r.Body.Length);
        WriteBytes(Encoding.ASCII.GetBytes(new string('a', 16385)));
        Assert.Equal(new FactsPromptText("", FactsSource.None), Make().ReadForPrompt());
    }

    // 10
    [Fact]
    public void Bom_and_invalid_utf8()
    {
        WriteBytes([0xEF, 0xBB, 0xBF, (byte)'H', (byte)'i']);
        Assert.Equal(new FactsPromptText("Hi", FactsSource.File), Make().ReadForPrompt());
        WriteBytes([0xFF, 0xFE, 0x41]);
        Assert.Equal(new FactsPromptText("", FactsSource.None), Make().ReadForPrompt());
    }

    // 11
    [Fact]
    public void Missing_file_and_directory()
    {
        Assert.Equal(new FactsPromptText("", FactsSource.None), Make().ReadForPrompt());
        Directory.CreateDirectory(Paths.File);
        Assert.Equal(new FactsPromptText("", FactsSource.None), Make().ReadForPrompt());
    }

    // 12
    [Fact]
    [Trait("os", "windows")]
    public void Exclusively_locked_file_is_none()
    {
        Write("x");
        using var fs = new FileStream(Paths.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(FactsSource.None, Make().ReadForPrompt().Source);
    }

    // 13
    [Fact]
    public void Read_is_fresh()
    {
        var f = Make();
        Write("A");
        Assert.Equal("A", f.ReadForPrompt().Body);
        Write("B");
        Assert.Equal("B", f.ReadForPrompt().Body);
    }

    // 14
    [Fact]
    public void Create_template_once_creates_file_and_state()
    {
        var f = Make();
        var o = f.CreateTemplateOnce("Ann");
        Assert.Equal(FactsTemplateResult.Created, o.Result);
        Assert.Equal(new UTF8Encoding(false).GetBytes(FactsTemplate.Render("Ann")), File.ReadAllBytes(Paths.File));
        Assert.True(f.TemplateRecorded());
        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.StateFile));
        Assert.Equal(_time.GetUtcNow().ToString("O"), doc.RootElement.GetProperty("templateCreatedUtc").GetString());
    }

    // 15
    [Fact]
    public void Existing_file_is_untouched()
    {
        Write("my facts");
        var stamp = File.GetLastWriteTimeUtc(Paths.File);
        var bytes = File.ReadAllBytes(Paths.File);
        var f = Make();
        Assert.Equal(FactsTemplateResult.AlreadyExists, f.CreateTemplateOnce("Ann").Result);
        Assert.Equal(bytes, File.ReadAllBytes(Paths.File));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Paths.File));
        Assert.True(f.TemplateRecorded());
    }

    // 16
    [Fact]
    public void Deleted_file_is_not_brought_back_automatically()
    {
        var f = Make();
        Assert.Equal(FactsTemplateResult.Created, f.CreateTemplateOnce("Ann").Result);
        File.Delete(Paths.File);
        Assert.Equal(FactsTemplateResult.AlreadyDone, f.CreateTemplateOnce("Ann").Result);
        Assert.False(File.Exists(Paths.File));
        Assert.Equal(FactsTemplateResult.Created, f.CreateIfMissing("Ann").Result);
        Assert.Equal(FactsTemplate.Render("Ann"), File.ReadAllText(Paths.File));
    }

    // 17, 18
    [Fact]
    public void Create_if_missing_with_existing_file()
    {
        Write("mine");
        var f = Make();
        Assert.False(f.TemplateRecorded());
        Assert.Equal(FactsTemplateResult.AlreadyExists, f.CreateIfMissing("Ann").Result);
        Assert.Equal("mine", File.ReadAllText(Paths.File));
        Assert.True(f.TemplateRecorded());
        Assert.Equal(FactsTemplateResult.AlreadyDone, f.CreateTemplateOnce("Ann").Result);
    }

    // 19
    [Fact]
    public void Failure_writes_no_state()
    {
        var dir = Path.GetDirectoryName(Paths.File)!;
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        File.WriteAllText(dir, "in the way");
        var f = Make();
        var o = f.CreateTemplateOnce("Ann");
        Assert.Equal(FactsTemplateResult.Failed, o.Result);
        Assert.NotNull(o.ErrorType);
        Assert.False(File.Exists(Paths.StateFile));
        Assert.Equal(FactsTemplateResult.Failed, f.CreateIfMissing("Ann").Result);
    }

    // 20
    [Fact]
    public void Garbage_state_file_still_counts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.StateFile)!);
        File.WriteAllText(Paths.StateFile, "not json");
        var f = Make();
        Assert.True(f.TemplateRecorded());
        Assert.Equal(FactsTemplateResult.AlreadyDone, f.CreateTemplateOnce("Ann").Result);
        Assert.False(File.Exists(Paths.File));
    }

    [Fact]
    public void Status_texts()
    {
        Assert.Equal("Facts about you: filled in", FactsTexts.Status(FactsSource.File));
        Assert.Equal("Facts about you: template only", FactsTexts.Status(FactsSource.Empty));
        Assert.Equal("Facts about you: not found", FactsTexts.Status(FactsSource.None));
    }
}
