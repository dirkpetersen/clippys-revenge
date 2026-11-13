using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using ClippysRevenge.Core;
using Microsoft.Win32;

namespace ClippysRevenge.App;

/// <summary>Starts Outlook from the tray click (SPEC 6.2). The only place that launches Outlook.</summary>
internal static class OutlookLauncher
{
    internal const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE";

    public static string? FindOutlookExe()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = root.OpenSubKey(AppPathsKey);
                var path = OutlookAppPath.Normalize(k?.GetValue(null) as string);
                if (path is not null && File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Try the next registry view.
            }
        }
        return null;
    }

    public static bool TryStart()
    {
        var path = FindOutlookExe();
        if (path is null)
        {
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
