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

    // ---- API key ----

    private const string Key = "sk-ant-api03-Abc_DEF-123456";

    [Fact]
    public void ApiKey_MissingFile()
    {
        var s = Store();
        Assert.Null(s.LoadApiKey());
        Assert.Null(s.CachedApiKey);
        Assert.False(File.Exists(s.ApiKeyPath));
        Assert.Equal(Path.Combine(Dir, "anthropic-key.bin"), s.ApiKeyPath);
    }

    [Fact]
    public void ApiKey_SaveThenLoadInNewStore()
    {
        var a = Store();
        a.SaveApiKey(Key);
        Assert.True(File.Exists(a.ApiKeyPath));
        Assert.DoesNotContain(Key, Encoding.UTF8.GetString(File.ReadAllBytes(a.ApiKeyPath)));
        var b = Store();
        Assert.Equal(Key, b.LoadApiKey());
        Assert.Equal(Key, a.CachedApiKey);
        Assert.Equal(Key, b.CachedApiKey);
    }

    [Fact]
    public void ApiKey_Trims()
    {
        var s = Store();
        s.SaveApiKey("  abcdefgh12 \n");
        Assert.Equal("abcdefgh12", Store().LoadApiKey());
    }

    [Theory]
    [InlineData("", "The API key is empty.")]
    [InlineData("   ", "The API key is empty.")]
    [InlineData("abc def", "The API key must be a single line without spaces.")]
    [InlineData("abc\tdef", "The API key must be a single line without spaces.")]
    [InlineData("abc\u0001def", "The API key must be a single line without spaces.")]
    public void ApiKey_RejectsInvalid(string input, string message)
    {
        var s = Store();
        s.SaveApiKey(Key);
        var before = File.ReadAllBytes(s.ApiKeyPath);
        var ex = Assert.Throws<ArgumentException>(() => s.SaveApiKey(input));
        Assert.Equal(message, ex.Message);
        Assert.Equal(before, File.ReadAllBytes(s.ApiKeyPath));
    }

    [Fact]
    public void ApiKey_RejectsTooLong()
    {
        var s = Store();
        var input = new string('a', 513);
        var ex = Assert.Throws<ArgumentException>(() => s.SaveApiKey(input));
        Assert.StartsWith("The API key must be a single line without spaces.", ex.Message);
        Assert.DoesNotContain(input, ex.Message);
        Assert.False(File.Exists(s.ApiKeyPath));
        s.SaveApiKey(new string('a', 512));
    }

    [Fact]
    public void ApiKey_Remove()
    {
        var s = Store();
        s.SaveApiKey(Key);
        s.SaveApiKey(null);
        Assert.False(File.Exists(s.ApiKeyPath));
        Assert.Null(s.LoadApiKey());
        Assert.Null(s.CachedApiKey);
        s.SaveApiKey(null);
    }

    [Fact]
    public void ApiKey_UndecryptableOrBlankIsNull()
    {
        var s = Store();
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(s.ApiKeyPath, [1, 2, 3]);
        Assert.Null(s.LoadApiKey());
        Assert.True(File.Exists(s.ApiKeyPath));
        File.WriteAllBytes(s.ApiKeyPath, new XorProtector().Protect(Encoding.UTF8.GetBytes("   ")));
        Assert.Null(s.LoadApiKey());
    }

    [Fact]
    public void ApiKey_NotInSettingsAndNotCreatedByLoad()
    {
        var s = Store();
        var r = s.Load();
        Assert.False(File.Exists(s.ApiKeyPath));
        s.SaveApiKey(Key);
        Assert.DoesNotContain(Key, File.ReadAllText(s.SettingsPath));
        s.Save(r.Settings);
        Assert.DoesNotContain(Key, File.ReadAllText(s.SettingsPath));
    }

    [Fact]
    public void ApiKey_OverwriteLeavesNoTmp()
    {
        var s = Store();
        s.SaveApiKey("key-one-12345");
        s.SaveApiKey("key-two-12345");
        Assert.Equal("key-two-12345", Store().LoadApiKey());
        Assert.False(File.Exists(s.ApiKeyPath + ".tmp"));
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
    public void Load_MigratesSendToDraftOnly()
    {
        var s = Store();
        s.Load();
        File.WriteAllText(s.SettingsPath, "{\"MeetingInviteMode\":\"Send\",\"InviteConfirmTimeoutSeconds\":120}");
        var before = File.ReadAllBytes(s.SettingsPath);
        var r = s.Load();
        Assert.Equal(MeetingInviteMode.DraftOnly, r.Settings.MeetingInviteMode);
        Assert.Empty(r.Warnings);
        Assert.False(r.FirstRun);
        Assert.False(File.Exists(s.SettingsPath + ".bad"));
        Assert.Equal(before, File.ReadAllBytes(s.SettingsPath));

        s.Save(r.Settings);
        var text = File.ReadAllText(s.SettingsPath);
        Assert.Contains("\"DraftOnly\"", text);
        Assert.DoesNotContain("\"Send\"", text);
        Assert.DoesNotContain("InviteConfirmTimeoutSeconds", text);
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

    [Theory]
    [InlineData("sk-ant-api03-abc", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a\tb", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\u0001b", false)]
    public void IsValidApiKeyFormat_Cases(string key, bool expected) =>
        Assert.Equal(expected, SettingsStore.IsValidApiKeyFormat(key));

    [Fact]
    public void IsValidApiKeyFormat_Length_And_Null()
    {
        Assert.True(SettingsStore.IsValidApiKeyFormat(new string('a', 512)));
        Assert.False(SettingsStore.IsValidApiKeyFormat(new string('a', 513)));
        Assert.Throws<ArgumentNullException>(() => SettingsStore.IsValidApiKeyFormat(null!));
    }
}
