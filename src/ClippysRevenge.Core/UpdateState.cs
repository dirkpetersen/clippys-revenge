using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public enum UpdateMode { Install, Notify, Off }

/// <summary>Install-folder layout (SPEC 20.6).</summary>
public sealed record UpdatePaths(string InstallDir, string ExePath, string OldExePath, string StagingDir, string StateFile)
{
    public const string ExeFileName = "ClippysRevenge.exe";
    public const string OldSuffix = ".old";
    public const string PartialSuffix = ".partial";
    public const string StateFileName = "update-state.json";

    private static readonly Regex StagedGrammar = new(
        @"^ClippysRevenge-(.+)\.exe\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>InstallDir = &lt;lad&gt;\Programs\ClippysRevenge, ExePath = InstallDir\ClippysRevenge.exe,
    /// OldExePath = ExePath + ".old", StagingDir = InstallDir\update, StateFile = &lt;lad&gt;\ClippysRevenge\update-state.json.</summary>
    /// <exception cref="ArgumentException">localAppData is null, blank or not fully qualified.</exception>
    public static UpdatePaths Default(string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
        {
            throw new ArgumentException("Must be a fully qualified path.", nameof(localAppData));
        }
        var installDir = Path.Combine(localAppData, "Programs", "ClippysRevenge");
        var exe = Path.Combine(installDir, ExeFileName);
        return new UpdatePaths(
            installDir,
            exe,
            exe + OldSuffix,
            Path.Combine(installDir, "update"),
            Path.Combine(localAppData, "ClippysRevenge", StateFileName));
    }

    /// <summary>"ClippysRevenge-&lt;v&gt;.exe".</summary>
    public static string StagedFileName(SemVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return "ClippysRevenge-" + version + ".exe";
    }

    /// <summary>Reverse of StagedFileName for an exact match with a valid SemVer.</summary>
    public static bool TryParseStagedFileName(string? fileName, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (fileName is null || fileName.Length > SemVersion.MaxLength + 32)
        {
            return false;
        }
        try
        {
            var m = StagedGrammar.Match(fileName);
            return m.Success && SemVersion.TryParse(m.Groups[1].Value, out version);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public string StagedExePath(SemVersion version) => Path.Combine(StagingDir, StagedFileName(version));

    public string PartialPath(SemVersion version) => StagedExePath(version) + PartialSuffix;

    /// <summary>Path.GetFullPath(processPath) equals Path.GetFullPath(ExePath), OrdinalIgnoreCase. False for
    /// null/blank or a path GetFullPath rejects. Never throws.</summary>
    public bool IsInstalledCopy(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }
        try
        {
            return string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(ExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public static class UpdatePolicy
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(24);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan EvaluateInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan StartupWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StartupIdleWait = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ManualIdleWait = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    /// <summary>SPEC 20.8 due rule.</summary>
    /// <exception cref="ArgumentNullException">state is null.</exception>
    public static bool IsDue(UpdateState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var lastCheck = Plausible(state.LastCheckUtc, now);
        var lastAttempt = Plausible(state.LastAttemptUtc, now);
        return (lastCheck is null || now - lastCheck >= CheckInterval)
            && (lastAttempt is null || now - lastAttempt >= RetryAfterFailure);
    }

    private static DateTimeOffset? Plausible(DateTimeOffset? stored, DateTimeOffset now) =>
        stored is { } s && s - now > FutureTolerance ? null : stored;

    /// <summary>For automatic installs: not a pre-release, newer than running, and not the failed version
    /// (an unparseable failedVersion is ignored).</summary>
    public static bool IsUpdate(SemVersion candidate, SemVersion running, string? failedVersion)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(running);
        if (!IsNewer(candidate, running))
        {
            return false;
        }
        return !(SemVersion.TryParse(failedVersion, out var failed) && candidate == failed);
    }

    /// <summary>For Update now: not a pre-release and newer than running (the failed version is allowed).</summary>
    public static bool IsNewer(SemVersion candidate, SemVersion running)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(running);
        return !candidate.IsPreRelease && candidate > running;
    }

    /// <summary>0 &lt;= now - startedUtc &lt; StartupWindow.</summary>
    public static bool InStartupWindow(DateTimeOffset startedUtc, DateTimeOffset now)
    {
        var age = now - startedUtc;
        return age >= TimeSpan.Zero && age < StartupWindow;
    }
}

/// <param name="Version">SemVersion.ToString() form, e.g. "0.0.6".</param>
/// <param name="Sha256">64 lower-case hex.</param>
/// <param name="Size">Size of the staged file in bytes.</param>
public sealed record StagedUpdate(string Version, string Sha256, long Size);

public sealed record PendingInstall(string FromVersion, string ToVersion, DateTimeOffset StartedUtc, bool Confirmed);

public enum UpdateNoticeKind { Updated, RolledBack }

/// <summary>For RolledBack: FromVersion = the version that failed, ToVersion = the version running again (SPEC 20.7).
/// For Updated: FromVersion = the old version, ToVersion = the new one.</summary>
public sealed record UpdateNotice(UpdateNoticeKind Kind, string FromVersion, string ToVersion);

/// <summary>update-state.json (SPEC 20.7). Mutable; UpdateStateStore hands out copies.</summary>
public sealed class UpdateState
{
    public const int MaxErrorLength = 300;

    public DateTimeOffset? LastCheckUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }
    public string? LatestTag { get; set; }
    public DateTimeOffset? LatestPublishedUtc { get; set; }
    public StagedUpdate? Staged { get; set; }
    public string? FailedVersion { get; set; }
    public string? NotifiedVersion { get; set; }
    public PendingInstall? Pending { get; set; }
    public UpdateNotice? Notice { get; set; }

    /// <summary>The release version of LatestTag, or null.</summary>
    [JsonIgnore]
    public SemVersion? LatestVersion => SemVersion.TryParseReleaseTag(LatestTag, out var v) ? v : null;

    /// <summary>Deep copy.</summary>
    public UpdateState Clone() => new()
    {
        LastCheckUtc = LastCheckUtc,
        LastAttemptUtc = LastAttemptUtc,
        LastError = LastError,
        LatestTag = LatestTag,
        LatestPublishedUtc = LatestPublishedUtc,
        Staged = Staged is null ? null : Staged with { },
        FailedVersion = FailedVersion,
        NotifiedVersion = NotifiedVersion,
        Pending = Pending is null ? null : Pending with { },
        Notice = Notice is null ? null : Notice with { },
    };
}

public sealed class UpdateStateStore
{
    public const int MaxFileBytes = 65_536;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ILogger? _log;
    private readonly Lock _lock = new();

    /// <exception cref="ArgumentException">path is null or blank.</exception>
    public UpdateStateStore(string path, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _log = log;
    }

    public string Path { get; }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    /// <summary>Never throws. Missing file gives an empty state (no log). Unreadable, too large or invalid JSON
    /// gives an empty state and one Warning. Invalid fields are dropped one by one.</summary>
    public UpdateState Load()
    {
        if (LoadCore(out var state, out var failure))
        {
            return state;
        }
        _log?.LogWarning("update-state.json could not be read ({Type}); using an empty update state.", failure);
        return state;
    }

    /// <summary>Like Load, but returns false (state = empty) when the file exists and can't be read or parsed.
    /// Missing file gives true with an empty state.</summary>
    public bool TryLoad(out UpdateState state) => LoadCore(out state, out _);

    private bool LoadCore(out UpdateState state, out string failure)
    {
        failure = "";
        state = new UpdateState();
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists)
            {
                return true;
            }
            if (info.Length > MaxFileBytes)
            {
                failure = "TooLarge";
                return false;
            }
            var bytes = File.ReadAllBytes(Path);
            if (bytes.Length > MaxFileBytes)
            {
                failure = "TooLarge";
                return false;
            }
            var read = JsonSerializer.Deserialize<UpdateState>(bytes, JsonOptions);
            if (read is null)
            {
                failure = "Null";
                return false;
            }
            state = Sanitize(read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or System.Security.SecurityException)
        {
            failure = ex.GetType().Name;
            state = new UpdateState();
            return false;
        }
    }

    /// <summary>Atomic: writes Path + ".tmp", then File.Move(tmp, Path, overwrite: true). Creates the folder.</summary>
    /// <exception cref="IOException">and UnauthorizedAccessException, unchanged.</exception>
    public void Save(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var clean = Sanitize(state.Clone());
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(clean, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>In-process lock; Load, change, Save; returns a copy of the saved state.</summary>
    /// <exception cref="IOException">and UnauthorizedAccessException from Save, unchanged.</exception>
    public UpdateState Update(Action<UpdateState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            var state = Load();
            change(state);
            Save(state);
            return Sanitize(state.Clone());
        }
    }

    private static UpdateState Sanitize(UpdateState s)
    {
        if (!SemVersion.TryParseReleaseTag(s.LatestTag, out _))
        {
            s.LatestTag = null;
            s.LatestPublishedUtc = null;
        }
        s.FailedVersion = Normalize(s.FailedVersion);
        s.NotifiedVersion = Normalize(s.NotifiedVersion);

        if (s.Staged is { } st)
        {
            s.Staged = SemVersion.TryParse(st.Version, out var sv)
                && UpdateHash.IsSha256Hex(st.Sha256)
                && st.Size >= ReleaseParser.MinExeBytes && st.Size <= ReleaseParser.MaxExeBytes
                ? new StagedUpdate(sv.ToString(), st.Sha256.ToLowerInvariant(), st.Size)
                : null;
        }
        if (s.Pending is { } p)
        {
            s.Pending = SemVersion.TryParse(p.FromVersion, out var from) && SemVersion.TryParse(p.ToVersion, out var to)
                ? p with { FromVersion = from.ToString(), ToVersion = to.ToString() }
                : null;
        }
        if (s.Notice is { } n)
        {
            s.Notice = Enum.IsDefined(n.Kind)
                && SemVersion.TryParse(n.FromVersion, out var from) && SemVersion.TryParse(n.ToVersion, out var to)
                ? n with { FromVersion = from.ToString(), ToVersion = to.ToString() }
                : null;
        }
        s.LastError = CleanError(s.LastError);
        return s;
    }

    private static string? Normalize(string? version) =>
        SemVersion.TryParse(version, out var v) ? v.ToString() : null;

    private static string? CleanError(string? error)
    {
        if (error is null)
        {
            return null;
        }
        var sb = new StringBuilder(error.Length);
        foreach (var c in error)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        var text = sb.ToString().Trim();
        if (text.Length > UpdateState.MaxErrorLength)
        {
            text = text[..UpdateState.MaxErrorLength];
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
