using System.Diagnostics;

namespace ClippysRevenge.Core.Tests;

/// <summary>Proves the Windows behaviour SPEC 20.9 relies on: a running exe can be renamed but not deleted.</summary>
public sealed class RunningExeRenameTests
{
    [Fact]
    [Trait("os", "windows")]
    public async Task Running_exe_can_be_renamed_but_not_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var dir = Path.Combine(Path.GetTempPath(), "clippy-ren-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ClippysRevenge.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path);
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(path, "-n 30 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(proc);

            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));
            File.Move(path, path + ".old");
            Assert.True(File.Exists(path + ".old"));
            Assert.False(File.Exists(path));
            File.WriteAllBytes(path, "MZnew"u8.ToArray());
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (proc is not null)
            {
                try
                {
                    proc.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                await proc.WaitForExitAsync(TestContext.Current.CancellationToken);
                proc.Dispose();
            }
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
