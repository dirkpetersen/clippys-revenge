namespace ClippysRevenge.Core.Tests;

public class InstallScriptTests
{
    private static string ReadScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "install.ps1")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "install.ps1"));
    }

    [Fact]
    public void Script_HandlesUninstallAndPurge()
    {
        var text = ReadScript();
        Assert.Contains("[switch]$Uninstall", text);
        Assert.Contains("[switch]$Purge", text);
        Assert.Contains("SHA256SUMS.txt", text);
        Assert.Contains("CurrentVersion\\Run", text);
    }

    [Fact]
    public void Script_NeverReferencesOutlookExeOrToken()
    {
        var text = ReadScript();
        Assert.DoesNotContain("outlook.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token.bin", text, StringComparison.OrdinalIgnoreCase);
    }
}
