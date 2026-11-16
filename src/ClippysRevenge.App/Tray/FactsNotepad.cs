using System.Diagnostics;

namespace ClippysRevenge.App.Tray;

/// <summary>Opens the facts file in Notepad (SPEC 25.2): notepad.exe with the full path as one ArgumentList element.
/// No shell, no command line to quote. The only Process.Start besides the ones in SPEC 4/19/20.</summary>
internal static class FactsNotepad
{
    /// <exception cref="System.ComponentModel.Win32Exception">notepad.exe couldn't be started.</exception>
    public static void Open(string path)
    {
        var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(path);
        Process.Start(psi)?.Dispose();
    }
}
