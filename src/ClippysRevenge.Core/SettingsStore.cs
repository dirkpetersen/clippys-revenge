using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClippysRevenge.Core;

/// <summary>Encrypts the token for storage (DPAPI in the App; fake in tests).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    /// <exception cref="CryptographicException">Not decryptable.</exception>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Outcome of <see cref="SettingsStore.Load"/>.</summary>
/// <param name="Settings">Always valid.</param>
/// <param name="Token">Raw token; never log it.</param>
/// <param name="FirstRun">settings.json did not exist.</param>
/// <param name="Warnings">Recovery notes (no secrets).</param>
public sealed record SettingsLoadResult(Settings Settings, string Token, bool FirstRun, IReadOnlyList<string> Warnings)
{
    public override string ToString() => $"SettingsLoadResult(FirstRun={FirstRun})";
}

/// <summary>Reads and writes settings.json and token.bin in one folder (SPEC 5.1).</summary>
public sealed class SettingsStore
{
    public const string SettingsFileName = "settings.json";
    public const string TokenFileName = "token.bin";

    private readonly ISecretProtector _protector;

    public SettingsStore(string dataDir, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentNullException.ThrowIfNull(protector);
        DataDir = dataDir;
        _protector = protector;
    }

    public string DataDir { get; }
    public string SettingsPath => Path.Combine(DataDir, SettingsFileName);
    public string TokenPath => Path.Combine(DataDir, TokenFileName);

    /// <summary>
    /// Creates the folder, then loads or creates settings and token. A corrupt or invalid
    /// settings.json is renamed to settings.json.bad and replaced by defaults; an unreadable
    /// token.bin is replaced by a new token.
    /// </summary>
    public SettingsLoadResult Load()
    {
        Directory.CreateDirectory(DataDir);
        var warnings = new List<string>();
        bool first = !File.Exists(SettingsPath);
        Settings settings;
        if (first)
        {
            settings = new Settings();
            Save(settings);
        }
        else
        {
            settings = ReadSettings(warnings);
        }
        return new SettingsLoadResult(settings, LoadOrCreateToken(warnings), first, warnings);
    }

    private Settings ReadSettings(List<string> warnings)
    {
        string problem;
        try
        {
            var s = Settings.FromJson(File.ReadAllText(SettingsPath, Encoding.UTF8));
            var errors = s.Validate();
            if (errors.Count == 0) return s;
            problem = "invalid for " + string.Join(", ", errors.Select(e => e.Field));
        }
        catch (JsonException)
        {
            problem = "not valid JSON";
        }
        File.Move(SettingsPath, SettingsPath + ".bad", overwrite: true);
        warnings.Add($"settings.json was {problem}; it was kept as settings.json.bad and defaults were restored.");
        var d = new Settings();
        Save(d);
        return d;
    }

    private string LoadOrCreateToken(List<string> warnings)
    {
        if (File.Exists(TokenPath))
        {
            try
            {
                var token = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(TokenPath)));
                if (!string.IsNullOrWhiteSpace(token)) return token;
            }
            catch (CryptographicException) { }
            warnings.Add("token.bin could not be read; a new token was generated.");
        }
        var fresh = AuthToken.Generate();
        SaveToken(fresh);
        return fresh;
    }

    /// <summary>Validates, then writes atomically. Throws ArgumentException (field names only) if invalid.</summary>
    public void Save(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0)
            throw new ArgumentException("Settings are invalid: " + string.Join(", ", errors.Select(e => e.Field)), nameof(settings));
        Directory.CreateDirectory(DataDir);
        WriteAtomic(SettingsPath, Encoding.UTF8.GetBytes(settings.ToJson()));
    }

    public void SaveToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Directory.CreateDirectory(DataDir);
        WriteAtomic(TokenPath, _protector.Protect(Encoding.UTF8.GetBytes(token)));
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
