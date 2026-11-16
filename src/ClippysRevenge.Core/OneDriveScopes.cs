using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>Raw string values of one subkey of HKCU\Software\SyncEngines\Providers\OneDrive (SPEC 26.3).
/// App passes `key.GetValue(name)?.ToString()`; Core validates.</summary>
public sealed record OneDriveScopeValues(string? MountPoint, string? UrlNamespace, string? LibraryType,
    string? IsFolderScope, string? FullRemotePath);

/// <summary>HKCU\Software\Microsoft\OneDrive\Accounts\Personal values UserFolder and cid (consumer fallback).</summary>
public sealed record OneDrivePersonalValues(string? UserFolder, string? Cid);

/// <summary>The registry reads of SPEC 26.3, implemented in App. Read-only.
/// Members may throw; Build turns that into an empty list.</summary>
public interface IOneDriveRegistryData
{
    IReadOnlyList<OneDriveScopeValues> ReadScopes();

    /// <summary>Null when the key or a value is missing.</summary>
    OneDrivePersonalValues? ReadPersonal();
}

public static partial class OneDriveScopes
{
    private const string ConsumerPrefix = "https://d.docs.live.net/";

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^[0-9A-Za-z]{1,32}$")]
    private static partial Regex CidPattern();

    /// <summary>SPEC 26.3 "Sync data". Never throws: any exception from `data` gives an empty list and one
    /// Warning with the exception type name only. directoryExists defaults to Directory.Exists.</summary>
    public static IReadOnlyList<SyncedRoot> Build(IOneDriveRegistryData data, ILogger? logger = null,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var exists = directoryExists ?? Directory.Exists;

        IReadOnlyList<OneDriveScopeValues> scopes;
        OneDrivePersonalValues? personal;
        try
        {
            scopes = data.ReadScopes();
            personal = data.ReadPersonal();
        }
        catch (Exception e)
        {
            logger?.LogWarning("OneDrive sync scopes could not be read ({ErrorType})", e.GetType().Name);
            return [];
        }

        var roots = new List<SyncedRoot>();
        foreach (var s in scopes ?? [])
        {
            if (s is null) continue;
            var folder = s.IsFolderScope?.Trim() == "1";
            var raw = folder && !string.IsNullOrWhiteSpace(s.FullRemotePath) ? s.FullRemotePath : s.UrlNamespace;
            var prefix = ValidPrefix(raw);
            var mount = ValidMount(s.MountPoint, exists);
            if (prefix is null || mount is null) continue;
            roots.Add(new SyncedRoot(prefix, mount, s.LibraryType?.Trim() ?? "", folder));
        }

        if (personal is not null && !string.IsNullOrWhiteSpace(personal.UserFolder)
            && personal.Cid is not null && CidPattern().IsMatch(personal.Cid)
            && !roots.Exists(r => r.UrlPrefix.StartsWith(ConsumerPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            var mount = ValidMount(personal.UserFolder, exists);
            if (mount is not null)
                roots.Add(new SyncedRoot($"{ConsumerPrefix}{personal.Cid}/", mount, "personal", false));
        }

        var result = new List<SyncedRoot>();
        foreach (var r in roots)
        {
            if (result.Exists(x => x.UrlPrefix.Equals(r.UrlPrefix, StringComparison.OrdinalIgnoreCase)
                                   && x.MountPoint.Equals(r.MountPoint, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(r);
        }
        return result;
    }

    private static string? ValidPrefix(string? raw)
    {
        var p = raw?.Trim();
        if (string.IsNullOrEmpty(p)) return null;
        if (!p.EndsWith('/')) p += "/";
        if (!Uri.TryCreate(p, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !OneDriveLinks.IsMicrosoftFileHost(uri)) return null;
        return p;
    }

    private static string? ValidMount(string? raw, Func<string, bool> exists)
    {
        var m = raw?.Trim();
        if (string.IsNullOrEmpty(m) || !DrivePath().IsMatch(m)) return null;
        foreach (var c in m)
            if (char.IsControl(c)) return null;
        foreach (var seg in m.Split('\\', '/'))
            if (seg is "." or "..") return null;
        try
        {
            return exists(m) ? m : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
