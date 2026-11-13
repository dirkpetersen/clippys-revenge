using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class ClaudeCliArgsTests
{
    private static readonly string Sp = Path.Combine(Path.GetTempPath(), "sp-test.md");

    private static string Pretty() =>
        JsonSerializer.Serialize(JsonDocument.Parse(DraftModel.ReplySchema).RootElement,
            new JsonSerializerOptions { WriteIndented = true });

    private static string[] Expected(bool safe)
    {
        var l = new List<string>
        {
            "-p", "--model", "claude-opus-5-5", "--effort", "medium", "--output-format", "json",
            "--json-schema", DraftModel.ReplySchema, "--tools", "", "--strict-mcp-config",
            "--no-session-persistence", "--system-prompt-file", Sp, "--disable-slash-commands",
        };
        if (safe) l.Add("--safe-mode");
        return [.. l];
    }

    [Fact]
    public void Build_ExactList() =>
        Assert.Equal(Expected(false), ClaudeCliArgs.Build("claude-opus-5-5", "medium", Pretty(), Sp, false));

    [Fact]
    public void Build_SafeMode() =>
        Assert.Equal(Expected(true), ClaudeCliArgs.Build("claude-opus-5-5", "medium", Pretty(), Sp, true));

    [Fact]
    public void Build_NeverContainsForbidden()
    {
        var a = ClaudeCliArgs.Build("claude-opus-5-5", "medium", DraftModel.StyleSchema, Sp, true);
        foreach (var bad in new[] { "--bare", "--mcp-config", "--allowedTools", "--add-dir", "--resume", "--continue",
                     "--dangerously-skip-permissions", "login" })
            Assert.DoesNotContain(bad, a);
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--bare")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("ü")]
    public void Build_BadModel_Throws(string m)
    {
        var ex = Assert.Throws<ArgumentException>(() => ClaudeCliArgs.Build(m, "low", DraftModel.ReplySchema, Sp, false));
        if (m.Length > 0) Assert.DoesNotContain(m, ex.Message);
    }

    [Fact]
    public void Build_LongModel_Throws() =>
        Assert.Throws<ArgumentException>(() => ClaudeCliArgs.Build(new string('a', 65), "low", DraftModel.ReplySchema, Sp, false));

    [Theory]
    [InlineData("claude-opus-4-6[1m]")]
    [InlineData("sonnet")]
    [InlineData("a.b_c-1")]
    public void Build_GoodModel(string m) =>
        Assert.Contains(m, ClaudeCliArgs.Build(m, "low", DraftModel.ReplySchema, Sp, false));

    [Theory]
    [InlineData("Medium")]
    [InlineData("ultra")]
    [InlineData("")]
    public void Build_BadEffort_Throws(string e) =>
        Assert.Throws<ArgumentException>(() => ClaudeCliArgs.Build("sonnet", e, DraftModel.ReplySchema, Sp, false));

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void Build_GoodEffort(string e) =>
        Assert.Contains(e, ClaudeCliArgs.Build("sonnet", e, DraftModel.ReplySchema, Sp, false));

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{bad")]
    public void Build_BadSchema_Throws(string s) =>
        Assert.Throws<ArgumentException>(() => ClaudeCliArgs.Build("sonnet", "low", s, Sp, false));

    [Fact]
    public void Build_RelativePromptFile_Throws() =>
        Assert.Throws<ArgumentException>(() => ClaudeCliArgs.Build("sonnet", "low", DraftModel.ReplySchema, "sp.md", false));

    [Fact]
    public void HelpMentions_WholeTokens()
    {
        Assert.True(ClaudeCliArgs.HelpMentions("  --system-prompt <prompt>", "--system-prompt"));
        Assert.True(ClaudeCliArgs.HelpMentions("--system-prompt[-file]", "--system-prompt"));
        Assert.False(ClaudeCliArgs.HelpMentions("--system-prompt-snapshot <on>", "--system-prompt"));
        Assert.False(ClaudeCliArgs.HelpMentions("--toolsx", "--tools"));
        Assert.True(ClaudeCliArgs.HelpMentions("--tools <tools...>", "--tools"));
    }

    [Fact]
    public void Constants()
    {
        Assert.Equal(["auth", "status", "--json"], ClaudeCliArgs.AuthStatus);
        Assert.Equal(["--json-schema", "--no-session-persistence", "--strict-mcp-config", "--tools", "--effort", "--system-prompt"],
            ClaudeCliArgs.RequiredHelpFlags);
    }
}
