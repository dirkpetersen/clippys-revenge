using System.Security.Cryptography;
using System.Text;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] p) => p.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xAB).ToArray();
        public byte[] Unprotect(byte[] d)
        {
            if (d.Length == 0 || d[0] != 0xAB) throw new CryptographicException("bad");
            return d.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray();
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-ss-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "data");
    private SettingsStore Store() => new(Dir, new XorProtector());

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public void FirstRun_CreatesFolderSettingsAndToken()
    {
        var r = Store().Load();
        Assert.True(r.FirstRun);
        Assert.Empty(r.Warnings);
        Assert.Matches("^[0-9a-f]{64}$", r.Token);
        Assert.True(File.Exists(Path.Combine(Dir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Dir, "token.bin")));
        Assert.Equal(Settings.DefaultPort, r.Settings.Port);
    }

    [Fact]
    public void SecondLoad_NotFirstRun_SameToken_SameSettings()
    {
        var a = Store().Load();
        var b = Store().Load();
        Assert.False(b.FirstRun);
        Assert.Equal(a.Token, b.Token);
        Assert.Equal(a.Settings.ToJson(), b.Settings.ToJson());
    }

    [Fact]
    public void Token_NotInSettingsJson_NorStoredInPlain()
    {
        var r = Store().Load();
        Assert.DoesNotContain(r.Token, File.ReadAllText(Path.Combine(Dir, "settings.json")));
        Assert.DoesNotContain(r.Token, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Dir, "token.bin"))));
    }

    [Fact]
    public void Save_RoundTrips()
    {
        var s = Store();
        s.Load();
        s.Save(new Settings { Port = 9000, RetentionMonths = 6 });
        var r = Store().Load();
        Assert.Equal(9000, r.Settings.Port);
        Assert.Equal(6, r.Settings.RetentionMonths);
    }

    [Fact]
    public void Save_Invalid_ThrowsWithoutWriting()
    {
        var s = Store();
        s.Load();
        var before = File.ReadAllText(s.SettingsPath);
        Assert.Throws<ArgumentException>(() => s.Save(new Settings { Port = 5 }));
        Assert.Equal(before, File.ReadAllText(s.SettingsPath));
        Assert.False(File.Exists(s.SettingsPath + ".tmp"));
    }

    [Fact]
    public void CorruptJson_IsKeptAsBad_AndDefaultsRestored()
    {
        var s = Store();
        s.Load();
        File.WriteAllText(s.SettingsPath, "{ not json");
        var r = s.Load();
        Assert.False(r.FirstRun);
        Assert.Single(r.Warnings);
        Assert.Equal("{ not json", File.ReadAllText(s.SettingsPath + ".bad"));
        Assert.Equal(Settings.DefaultPort, r.Settings.Port);
        Assert.Empty(r.Settings.Validate());
    }

    [Fact]
    public void InvalidValues_AreKeptAsBad_AndDefaultsRestored()
    {
        var s = Store();
        s.Load();
        File.WriteAllText(s.SettingsPath, "{\"Port\": 80}");
        var r = s.Load();
        Assert.Single(r.Warnings);
        Assert.Contains("Port", r.Warnings[0]);
        Assert.True(File.Exists(s.SettingsPath + ".bad"));
        Assert.Equal(Settings.DefaultPort, r.Settings.Port);
    }

    [Fact]
    public void UnreadableToken_IsReplaced()
    {
        var s = Store();
        var a = s.Load();
        File.WriteAllBytes(s.TokenPath, [1, 2, 3]);
        var b = s.Load();
        Assert.NotEqual(a.Token, b.Token);
        Assert.Single(b.Warnings);
        Assert.DoesNotContain(b.Token, b.Warnings[0]);
        Assert.Equal(b.Token, Store().Load().Token);
    }

    [Fact]
    public void SaveToken_Persists()
    {
        var s = Store();
        s.Load();
        s.SaveToken("new-token-value");
        Assert.Equal("new-token-value", Store().Load().Token);
    }

    [Fact]
    public void ResultToString_HasNoToken()
    {
        var r = Store().Load();
        Assert.DoesNotContain(r.Token, r.ToString());
    }
}
