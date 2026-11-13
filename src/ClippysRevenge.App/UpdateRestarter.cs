using System.Diagnostics;
using ClippysRevenge.Core;

namespace ClippysRevenge.App;

/// <summary>Starts the installed Clippy exe for an update handover (SPEC 20.11). The updater's only Process.Start.</summary>
internal static class UpdateRestarter
{
    /// <summary>Starts paths.ExePath with UseShellExecute = false, no arguments, WorkingDirectory = paths.InstallDir.</summary>
    /// <exception cref="ArgumentException">Path.GetFullPath(path) isn't Path.GetFullPath(paths.ExePath) (OrdinalIgnoreCase).</exception>
    /// <exception cref="InvalidOperationException">Process.Start returned null.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The exe couldn't be started.</exception>
    public static IUpdateProcess Start(UpdatePaths paths, string path)
    {
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(paths.ExePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The updater only starts the installed Clippy's Revenge exe.", nameof(path));
        }
        var p = Process.Start(new ProcessStartInfo(paths.ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = paths.InstallDir,
        }) ?? throw new InvalidOperationException("The new version did not start.");
        return new ProcessHandle(p);
    }

    /// <summary>FileVersionInfo.GetVersionInfo(path).ProductVersion, cut at the first '+', trimmed; null when blank or
    /// on any I/O / format error.</summary>
    public static string? ReadExeVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (string.IsNullOrWhiteSpace(v))
            {
                return null;
            }
            var plus = v.IndexOf('+');
            if (plus >= 0)
            {
                v = v[..plus];
            }
            v = v.Trim();
            return v.Length == 0 ? null : v;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private sealed class ProcessHandle(Process p) : IUpdateProcess
    {
        public bool HasExited => p.HasExited;

        public void Kill() => p.Kill();

        public Task WaitForExitAsync(CancellationToken ct) => p.WaitForExitAsync(ct);

        public void Dispose() => p.Dispose();
    }
}
