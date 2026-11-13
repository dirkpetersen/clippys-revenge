using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public class UpdateStateTests : IDisposable
{
    private const string Hex = "05c97b27c8fc33a1f11c4a658121397d7c309e6d64ccdce3f2bc9ff362b2b2f5";
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-upd-" + Guid.NewGuid().ToString("N"));

    public UpdateStateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private string StatePath => Path.Combine(_root, "sub", "update-state.json");

    private UpdateStateStore Store(ILogger? log = null) => new(StatePath, log);

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, text);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    // Paths

    [Fact]
    public void Paths_Default()
    {
        var p = UpdatePaths.Default(_root);
        var install = Path.Combine(_root, "Programs", "ClippysRevenge");
        Assert.Equal(install, p.InstallDir);
        Assert.Equal(Path.Combine(install, "ClippysRevenge.exe"), p.ExePath);
        Assert.Equal(Path.Combine(install, "ClippysRevenge.exe") + ".old", p.OldExePath);
        Assert.Equal(Path.Combine(install, "update"), p.StagingDir);
        Assert.Equal(Path.Combine(_root, "ClippysRevenge", "update-state.json"), p.StateFile);
        Assert.Throws<ArgumentException>(() => UpdatePaths.Default(""));
        Assert.Throws<ArgumentException>(() => UpdatePaths.Default("x"));
        Assert.Throws<ArgumentException>(() => UpdatePaths.Default(null!));
    }

    [Fact]
    public void Paths_StagedNames()
    {
        var v = SemVersion.Parse("0.0.6");
        Assert.Equal("ClippysRevenge-0.0.6.exe", UpdatePaths.StagedFileName(v));
        Assert.True(UpdatePaths.TryParseStagedFileName("ClippysRevenge-0.0.6.exe", out var a));
        Assert.Equal(v, a);
        Assert.True(UpdatePaths.TryParseStagedFileName("ClippysRevenge-0.0.7-beta.1.exe", out var b));
        Assert.Equal("0.0.7-beta.1", b.ToString());
        Assert.False(UpdatePaths.TryParseStagedFileName("ClippysRevenge.exe", out _));
        Assert.False(UpdatePaths.TryParseStagedFileName("ClippysRevenge-0.0.6.exe.partial", out _));
        Assert.False(UpdatePaths.TryParseStagedFileName("ClippysRevenge-x.exe", out _));
        Assert.False(UpdatePaths.TryParseStagedFileName("clippy-0.0.6.exe", out _));
        Assert.False(UpdatePaths.TryParseStagedFileName(null, out _));

        var p = UpdatePaths.Default(_root);
        Assert.Equal(Path.Combine(p.StagingDir, "ClippysRevenge-0.0.6.exe"), p.StagedExePath(v));
        Assert.Equal(p.StagedExePath(v) + ".partial", p.PartialPath(v));
    }

    [Fact]
    public void Paths_IsInstalledCopy()
    {
        var p = UpdatePaths.Default(_root);
        Assert.True(p.IsInstalledCopy(p.ExePath));
        Assert.True(p.IsInstalledCopy(p.ExePath.ToUpperInvariant()));
        Assert.True(p.IsInstalledCopy(Path.Combine(p.InstallDir, "update", "..", "ClippysRevenge.exe")));
        Assert.False(p.IsInstalledCopy(Path.Combine(_root, "other", "ClippysRevenge.exe")));
        Assert.False(p.IsInstalledCopy(null));
        Assert.False(p.IsInstalledCopy(""));
        Assert.False(p.IsInstalledCopy("\0"));
    }

    // Policy

    [Fact]
    public void IsDue_Rules()
    {
        Assert.True(UpdatePolicy.IsDue(new UpdateState(), Now));
        Assert.False(UpdatePolicy.IsDue(new UpdateState { LastCheckUtc = Now - TimeSpan.FromHours(6 * 24 + 23) }, Now));
        Assert.True(UpdatePolicy.IsDue(new UpdateState { LastCheckUtc = Now - TimeSpan.FromDays(7) }, Now));
        Assert.False(UpdatePolicy.IsDue(new UpdateState
        {
            LastCheckUtc = Now - TimeSpan.FromDays(8),
            LastAttemptUtc = Now - TimeSpan.FromHours(2),
        }, Now));
        Assert.True(UpdatePolicy.IsDue(new UpdateState
        {
            LastCheckUtc = Now - TimeSpan.FromDays(8),
            LastAttemptUtc = Now - TimeSpan.FromHours(24),
        }, Now));
        Assert.True(UpdatePolicy.IsDue(new UpdateState { LastCheckUtc = Now + TimeSpan.FromDays(2) }, Now));
        Assert.False(UpdatePolicy.IsDue(new UpdateState { LastCheckUtc = Now + TimeSpan.FromHours(12) }, Now));
        Assert.Throws<ArgumentNullException>(() => UpdatePolicy.IsDue(null!, Now));
    }

    [Fact]
    public void IsUpdate_And_IsNewer()
    {
        var running = SemVersion.Parse("0.0.5");
        Assert.True(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.6"), running, null));
        Assert.False(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.5"), running, null));
        Assert.False(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.4"), running, null));
        Assert.False(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.6-beta.1"), running, null));
        Assert.False(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.6"), running, "0.0.6"));
        Assert.True(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.7"), running, "0.0.6"));
        Assert.True(UpdatePolicy.IsUpdate(SemVersion.Parse("0.0.6"), running, "garbage"));

        Assert.True(UpdatePolicy.IsNewer(SemVersion.Parse("0.0.6"), running));
        Assert.False(UpdatePolicy.IsNewer(SemVersion.Parse("0.0.6-beta.1"), running));
        Assert.True(UpdatePolicy.IsNewer(SemVersion.Parse("0.0.6"), SemVersion.Parse("0.0.6-beta.1")));
    }

    [Fact]
    public void InStartupWindow_Edges()
    {
        Assert.True(UpdatePolicy.InStartupWindow(Now, Now));
        Assert.True(UpdatePolicy.InStartupWindow(Now, Now + new TimeSpan(0, 29, 59)));
        Assert.False(UpdatePolicy.InStartupWindow(Now, Now + new TimeSpan(0, 30, 0)));
        Assert.False(UpdatePolicy.InStartupWindow(Now, Now - TimeSpan.FromSeconds(1)));
    }

    // Store

    private static UpdateState Full() => new()
    {
        LastCheckUtc = new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero),
        LastAttemptUtc = new DateTimeOffset(2026, 11, 19, 8, 1, 0, TimeSpan.Zero),
        LastError = "boom",
        LatestTag = "v0.0.6",
        LatestPublishedUtc = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero),
        Staged = new StagedUpdate("0.0.6", Hex, 150_000_000),
        FailedVersion = "0.0.7",
        NotifiedVersion = "0.0.6",
        Pending = new PendingInstall("0.0.5", "0.0.6", new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), true),
        Notice = new UpdateNotice(UpdateNoticeKind.RolledBack, "0.0.7", "0.0.6"),
    };

    [Fact]
    public void Store_RoundTrip()
    {
        var store = Store();
        store.Save(Full());
        Assert.False(File.Exists(StatePath + ".tmp"));
        var s = store.Load();
        var f = Full();
        Assert.Equal(f.LastCheckUtc, s.LastCheckUtc);
        Assert.Equal(f.LastAttemptUtc, s.LastAttemptUtc);
        Assert.Equal(f.LastError, s.LastError);
        Assert.Equal(f.LatestTag, s.LatestTag);
        Assert.Equal(f.LatestPublishedUtc, s.LatestPublishedUtc);
        Assert.Equal(f.Staged, s.Staged);
        Assert.Equal(f.FailedVersion, s.FailedVersion);
        Assert.Equal(f.NotifiedVersion, s.NotifiedVersion);
        Assert.Equal(f.Pending, s.Pending);
        Assert.Equal(f.Notice, s.Notice);
        Assert.Equal("0.0.6", s.LatestVersion!.ToString());
    }

    [Fact]
    public void Store_Update_ReturnsSavedCopy_AndOtherStoreSeesIt()
    {
        var store = Store();
        var saved = store.Update(s => s.LatestTag = "v0.0.9");
        Assert.Equal("v0.0.9", saved.LatestTag);
        saved.LatestTag = "v1.0.0";
        Assert.Equal("v0.0.9", Store().Load().LatestTag);
        store.Update(s => s.FailedVersion = "0.0.9");
        var again = Store().Load();
        Assert.Equal("v0.0.9", again.LatestTag);
        Assert.Equal("0.0.9", again.FailedVersion);
    }

    [Fact]
    public void Store_Clone_IsDeep()
    {
        var a = Full();
        var b = a.Clone();
        b.LastError = "other";
        b.Staged = null;
        Assert.Equal("boom", a.Clone().LastError);
        Assert.NotNull(a.Staged);
        Assert.Equal(a.Pending, b.Pending);
    }

    [Fact]
    public void Store_MissingFile_IsEmptyAndTrue()
    {
        var log = new CapturingLogger();
        var store = Store(log);
        Assert.True(store.TryLoad(out var s));
        Assert.Null(s.LatestTag);
        Assert.Null(store.Load().LastCheckUtc);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Notice\":{\"Kind\":\"Exploded\",\"FromVersion\":\"0.0.1\",\"ToVersion\":\"0.0.2\"}}")]
    public void Store_BadFile_IsEmpty(string content)
    {
        WriteRaw(content);
        var log = new CapturingLogger();
        var store = Store(log);
        Assert.False(store.TryLoad(out var s));
        Assert.Null(s.LatestTag);
        Assert.Null(s.Notice);
        Assert.Empty(log.Entries);
        var loaded = store.Load();
        Assert.Null(loaded.LastError);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.StartsWith("update-state.json could not be read (", entry.Message);
    }

    [Fact]
    public void Store_TooLarge_IsEmpty()
    {
        WriteRaw("{\"LastError\":\"" + new string('a', 70_000) + "\"}");
        var store = Store();
        Assert.False(store.TryLoad(out var s));
        Assert.Null(s.LastError);
        Assert.Null(store.Load().LastError);
    }

    private static string Staged(string sha, long size) =>
        "{\"Staged\":{\"Version\":\"0.0.6\",\"Sha256\":\"" + sha + "\",\"Size\":" + size + "}}";

    [Fact]
    public void Store_Sanitizes_FieldByField()
    {
        WriteRaw("""{"LatestTag":"0.0.6","LatestPublishedUtc":"2026-11-13T00:00:00Z","FailedVersion":"0.0.7"}""");
        var s = Store().Load();
        Assert.Null(s.LatestTag);
        Assert.Null(s.LatestPublishedUtc);
        Assert.Equal("0.0.7", s.FailedVersion);

        WriteRaw(Staged(Hex[1..], 150000000));
        Assert.Null(Store().Load().Staged);

        WriteRaw(Staged(Hex, 10));
        Assert.Null(Store().Load().Staged);

        WriteRaw(Staged(Hex.ToUpperInvariant(), 150000000));
        Assert.Equal(Hex, Store().Load().Staged!.Sha256);

        WriteRaw("""{"Pending":{"FromVersion":"0.0.5","ToVersion":"x","StartedUtc":"2026-11-13T00:00:00Z","Confirmed":false}}""");
        Assert.Null(Store().Load().Pending);

        WriteRaw("""{"Notice":{"Kind":"Updated","FromVersion":"0.0.5","ToVersion":"nope"},"Unknown":5}""");
        Assert.Null(Store().Load().Notice);

        WriteRaw("""{"NotifiedVersion":"1.0.0+meta","FailedVersion":"junk"}""");
        var n = Store().Load();
        Assert.Equal("1.0.0", n.NotifiedVersion);
        Assert.Null(n.FailedVersion);
    }

    [Fact]
    public void Store_LastError_IsCleaned()
    {
        var json = JsonSerializer.Serialize(new { LastError = "  " + new string('x', 200) + "\n" + new string('y', 200) });
        WriteRaw(json);
        var e = Store().Load().LastError!;
        Assert.Equal(300, e.Length);
        Assert.DoesNotContain(e, char.IsControl);

        WriteRaw("""{"LastError":" \n\t "}""");
        Assert.Null(Store().Load().LastError);
    }

    [Fact]
    public void Store_Save_CreatesFolder_NoTmp_AndSanitizes()
    {
        var store = Store();
        Assert.False(Directory.Exists(Path.GetDirectoryName(StatePath)));
        store.Save(new UpdateState { FailedVersion = "nonsense", NotifiedVersion = "0.0.1" });
        Assert.True(File.Exists(StatePath));
        Assert.False(File.Exists(StatePath + ".tmp"));
        var text = File.ReadAllText(StatePath);
        Assert.DoesNotContain("FailedVersion", text);
        Assert.Contains("NotifiedVersion", text);
        Assert.Throws<ArgumentException>(() => new UpdateStateStore(" "));
    }

    [Fact]
    [Trait("os", "windows")]
    public void Store_LockedFile_IsEmpty()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Store().Save(Full());
        using var held = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var store = Store();
        Assert.False(store.TryLoad(out var s));
        Assert.Null(s.LatestTag);
        Assert.Null(store.Load().LatestTag);
    }
}
