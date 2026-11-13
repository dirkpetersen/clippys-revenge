using Microsoft.Win32;

namespace ClippysRevenge.App;

/// <summary>HKCU Run key for "Start with Windows" (SPEC 14).</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClippysRevenge";

    public static void Apply(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var path = exePath ?? Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) key.SetValue(ValueName, "\"" + path + "\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }
}
