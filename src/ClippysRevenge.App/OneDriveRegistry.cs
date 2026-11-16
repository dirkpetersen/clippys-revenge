using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClippysRevenge.App;

/// <summary>SPEC 26.3 "Sync data": HKCU reads, read-only, no other key.</summary>
internal sealed class OneDriveRegistry(ILogger? log = null, string providersKey = OneDriveRegistry.ProvidersKey,
    string personalKey = OneDriveRegistry.PersonalKey) : IOneDriveRegistryData
{
    internal const string ProvidersKey = @"Software\SyncEngines\Providers\OneDrive";
    internal const string PersonalKey = @"Software\Microsoft\OneDrive\Accounts\Personal";

    /// <summary>One record per subkey of providersKey (raw values via GetValue(name)?.ToString()); [] when the key
    /// is missing. May throw (Build turns that into []).</summary>
    public IReadOnlyList<OneDriveScopeValues> ReadScopes()
    {
        using var root = Registry.CurrentUser.OpenSubKey(providersKey);
        if (root is null) return [];
        var result = new List<OneDriveScopeValues>();
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k is null) continue;
            result.Add(new OneDriveScopeValues(Get(k, "MountPoint"), Get(k, "UrlNamespace"), Get(k, "LibraryType"),
                Get(k, "IsFolderScope"), Get(k, "FullRemotePath")));
        }
        return result;
    }

    /// <summary>UserFolder and cid; null when the key or either value is missing or blank.</summary>
    public OneDrivePersonalValues? ReadPersonal()
    {
        using var k = Registry.CurrentUser.OpenSubKey(personalKey);
        if (k is null) return null;
        var folder = Get(k, "UserFolder");
        var cid = Get(k, "cid");
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(cid)) return null;
        return new OneDrivePersonalValues(folder, cid);
    }

    /// <summary>OneDriveScopes.Build(this, log). Never throws.</summary>
    public IReadOnlyList<SyncedRoot> Read() => OneDriveScopes.Build(this, log);

    private static string? Get(RegistryKey k, string name) => k.GetValue(name)?.ToString();
}
