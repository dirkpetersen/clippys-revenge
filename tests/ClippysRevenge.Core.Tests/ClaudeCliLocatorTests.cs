namespace ClippysRevenge.Core.Tests;

public class ClaudeCliLocatorTests
{
    private const string PathEnv = "C:\\a;relative\\bin;\"C:\\b\";C:\\c";
    private const string Npm = "C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js";
    private const string Native = "C:\\Users\\u\\.local\\bin\\claude.exe";

    private static ClaudeCliLocator Win(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var env = new Dictionary<string, string>
        {
            ["PATH"] = PathEnv,
            ["USERPROFILE"] = "C:\\Users\\u",
            ["APPDATA"] = "C:\\Users\\u\\AppData\\Roaming",
        };
        return new ClaudeCliLocator(k => env.GetValueOrDefault(k), set.Contains, true);
    }

    [Fact]
    public void FirstPathHit_QuotesStripped()
    {
        var c = Win("C:\\b\\claude.exe", "C:\\c\\claude.exe").Find(null);
        Assert.Equal("C:\\b\\claude.exe", c!.FileName);
        Assert.Empty(c.PrefixArgs);
    }

    [Fact]
    public void RelativePathEntry_Skipped() => Assert.Null(Win("relative\\bin\\claude.exe").Find(null));

    [Fact]
    public void CmdShimIgnored_NativeWins() =>
        Assert.Equal(Native, Win("C:\\a\\claude.cmd", Native).Find(null)!.FileName);

    [Fact]
    public void Npm_UsesNode()
    {
        var c = Win(Npm, "C:\\c\\node.exe").Find(null)!;
        Assert.Equal("C:\\c\\node.exe", c.FileName);
        Assert.Equal([Npm], c.PrefixArgs);
    }

    [Fact]
    public void Npm_NoNode_Null() => Assert.Null(Win(Npm).Find(null));

    [Fact]
    public void ShimsOnly_Null() =>
        Assert.Null(Win("C:\\a\\claude.cmd", "C:\\a\\claude.bat", "C:\\a\\claude.ps1").Find(null));

    [Fact]
    public void Order_PathNativeNpm()
    {
        Assert.Equal("C:\\b\\claude.exe", Win("C:\\b\\claude.exe", Native, Npm, "C:\\c\\node.exe").Find(null)!.FileName);
        Assert.Equal(Native, Win(Native, Npm, "C:\\c\\node.exe").Find(null)!.FileName);
        Assert.Equal("C:\\c\\node.exe", Win(Npm, "C:\\c\\node.exe").Find(null)!.FileName);
    }

    [Fact]
    public void Configured_Wins()
    {
        var c = Win("D:\\tools\\claude.exe", "C:\\b\\claude.exe").Find("D:\\tools\\claude.exe")!;
        Assert.Equal("D:\\tools\\claude.exe", c.FileName);
        Assert.Empty(c.PrefixArgs);
    }

    [Fact]
    public void Configured_Missing_NoFallback() => Assert.Null(Win("C:\\b\\claude.exe").Find("D:\\tools\\claude.exe"));

    [Theory]
    [InlineData("D:\\x\\claude.cmd")]
    [InlineData("D:\\x\\claude.bat")]
    [InlineData("D:\\x\\claude.PS1")]
    [InlineData("D:\\x\\claude.txt")]
    public void Configured_BadExtension_Null(string p) => Assert.Null(Win(p).Find(p));

    [Theory]
    [InlineData("\\\\srv\\share\\claude.exe")]
    [InlineData("\\\\?\\C:\\claude.exe")]
    [InlineData("claude.exe")]
    [InlineData("C:claude.exe")]
    [InlineData("C:/x/claude.exe")]
    public void Configured_NotFullyQualified_Null(string p) => Assert.Null(Win(p).Find(p));

    [Fact]
    public void Configured_Js_UsesNode()
    {
        var c = Win("D:\\npm\\cli.js", "C:\\a\\node.exe").Find("D:\\npm\\cli.js")!;
        Assert.Equal("C:\\a\\node.exe", c.FileName);
        Assert.Equal(["D:\\npm\\cli.js"], c.PrefixArgs);
    }

    [Fact]
    public void Configured_Blank_RunsSearch() =>
        Assert.Equal("C:\\b\\claude.exe", Win("C:\\b\\claude.exe").Find("  ")!.FileName);

    [Fact]
    public void NonWindows()
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:rel:/opt/c/bin", ["HOME"] = "/home/u" };
        var a = new ClaudeCliLocator(k => env.GetValueOrDefault(k), new HashSet<string> { "/opt/c/bin/claude" }.Contains, false);
        Assert.Equal("/opt/c/bin/claude", a.Find(null)!.FileName);
        var b = new ClaudeCliLocator(k => env.GetValueOrDefault(k), new HashSet<string> { "/home/u/.local/bin/claude" }.Contains, false);
        Assert.Equal("/home/u/.local/bin/claude", b.Find(null)!.FileName);
    }

    [Fact]
    public void NullEnv_Null() =>
        Assert.Null(new ClaudeCliLocator(_ => null, _ => true, true).Find(null));

    [Fact]
    public void FileExistsThrows_CountsAsMissing()
    {
        var l = new ClaudeCliLocator(
            k => k == "PATH" ? "C:\\a;C:\\b" : null,
            p => p == "C:\\a\\claude.exe" ? throw new IOException("x") : p == "C:\\b\\claude.exe",
            true);
        Assert.Equal("C:\\b\\claude.exe", l.Find(null)!.FileName);
    }
}
