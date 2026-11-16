using System.Diagnostics;
using System.Security.AccessControl;
using ClippysRevenge.App;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Windows only; no Outlook (SPEC 26.9). Not a live test.</summary>
[Trait("os", "windows")]
public sealed class AiFilesWindowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-aifiles-win-" + Guid.NewGuid().ToString("N"));

    public AiFilesWindowsTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_root))
                if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
    }

    [Fact]
    public void PrepareTempPath_MakesAUserOnlyFolder()
    {
        var folder = Path.Combine(_root, "ai-files");
        var ai = new AiFiles(new AiFilesOptions { Folder = folder, EnsurePrivateFolder = PrivateFolder.Ensure });
        var p = ai.PrepareTempPath(false);
        PrivateFolderTests.AssertUserOnly(folder);
        File.WriteAllText(p, "x");
        PrivateFolderTests.AssertOnlyMe(new FileInfo(p).GetAccessControl(AccessControlSections.Access), directory: false);
        ai.Discard(p);
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void Junction_IsRefused()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var victim = Path.Combine(target, AiFilePaths.NewFileName(false));
        File.WriteAllText(victim, "x");
        var link = Path.Combine(_root, "ai-files");
        MakeJunction(link, target);

        var ai = new AiFiles(new AiFilesOptions { Folder = link, EnsurePrivateFolder = _ => { } });
        Assert.Throws<AiFolderUnusableException>(() => ai.PrepareTempPath(false));
        ai.CleanUpAtStart();
        Assert.True(File.Exists(victim));
    }
}
