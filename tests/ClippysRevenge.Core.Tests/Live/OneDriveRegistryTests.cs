using ClippysRevenge.App;
using Microsoft.Win32;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Windows only; no Outlook. Reads a throwaway HKCU test key; only this test code writes the registry.</summary>
[Trait("os", "windows")]
public sealed class OneDriveRegistryTests : IDisposable
{
    private readonly string _base = @"Software\ClippysRevengeTests\" + Guid.NewGuid().ToString("N");
    private readonly List<string> _dirs = [];

    public OneDriveRegistryTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_base, throwOnMissingSubKey: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        foreach (var d in _dirs)
        {
            try
            {
                Directory.Delete(d, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "clippy-odreg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    private string Providers => _base + @"\Providers";
    private string Personal => _base + @"\Personal";

    private OneDriveRegistry Make() => new(null, Providers, Personal);

    [Fact]
    public void ReadScopes_OneRecordPerSubkey()
    {
        var mountA = TempDir();
        var mountB = TempDir();
        using (var a = Registry.CurrentUser.CreateSubKey(Providers + @"\A"))
        {
            a.SetValue("MountPoint", mountA);
            a.SetValue("UrlNamespace", "https://contoso.sharepoint.com/sites/x/Shared Documents/");
            a.SetValue("LibraryType", "teamsite");
        }
        using (var b = Registry.CurrentUser.CreateSubKey(Providers + @"\B"))
        {
            b.SetValue("IsFolderScope", 1, RegistryValueKind.DWord);
            b.SetValue("FullRemotePath", "https://contoso.sharepoint.com/sites/x/Shared Documents/Sub");
            b.SetValue("MountPoint", mountB);
            b.SetValue("UrlNamespace", "https://contoso.sharepoint.com/sites/x/Shared Documents/");
        }
        using (Registry.CurrentUser.CreateSubKey(Providers + @"\C"))
        {
        }

        var reg = Make();
        var scopes = reg.ReadScopes();
        Assert.Equal(3, scopes.Count);
        Assert.Equal("1", scopes.Single(s => s.MountPoint == mountB).IsFolderScope);
        var c = scopes.Single(s => s.MountPoint is null);
        Assert.Null(c.UrlNamespace);
        Assert.Null(c.LibraryType);
        Assert.Null(c.IsFolderScope);
        Assert.Null(c.FullRemotePath);

        var roots = reg.Read();
        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, r => r.UrlPrefix.EndsWith("/Sub/", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingProvidersKey_IsEmpty()
    {
        var reg = Make();
        Assert.Empty(reg.ReadScopes());
        Assert.Empty(reg.Read());
    }

    [Fact]
    public void ReadPersonal_NeedsBothValues()
    {
        var reg = Make();
        Assert.Null(reg.ReadPersonal());

        using (var k = Registry.CurrentUser.CreateSubKey(Personal))
        {
            k.SetValue("cid", "abc123");
            Assert.Null(reg.ReadPersonal());
            k.SetValue("UserFolder", @"C:\Users\x\OneDrive");
        }
        var p = reg.ReadPersonal();
        Assert.NotNull(p);
        Assert.Equal(@"C:\Users\x\OneDrive", p!.UserFolder);
        Assert.Equal("abc123", p.Cid);
    }
}
