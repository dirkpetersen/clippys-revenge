using System.Reflection;
using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>Loads the embedded paperclip .ico resources once (SPEC 6.1).</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayIconKey, Icon> _icons = [];

    public TrayIcons()
    {
        var asm = Assembly.GetExecutingAssembly();
        var size = SystemInformation.SmallIconSize;
        try
        {
            foreach (var key in Enum.GetValues<TrayIconKey>())
            {
                var name = TrayIconArt.FileName(key);
                using var stream = asm.GetManifestResourceStream("ClippysRevenge.Icons." + name)
                    ?? throw new InvalidOperationException($"Embedded tray icon '{name}' is missing.");
                _icons[key] = new Icon(stream, size);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Icon Get(TrayIconKey key) => _icons[key];

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        _icons.Clear();
    }
}
