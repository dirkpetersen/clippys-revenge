using static ClippysRevenge.Core.Tests.GitHubFixture;
using Reply = ClippysRevenge.Core.Tests.FakeGitHubHandler.Reply;

namespace ClippysRevenge.Core.Tests;

/// <summary>Wraps the manual clock and counts timers made through it (the service's Task.Delay calls).</summary>
internal sealed class CountingTime(ManualTimeProvider inner) : TimeProvider
{
    private int _timers;

    public int TimerCount => Volatile.Read(ref _timers);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Increment(ref _timers);
        return inner.CreateTimer(callback, state, dueTime, period);
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly SemVersion V005 = SemVersion.Parse("0.0.5");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "clippy-m122s-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _manual = new();
    private readonly CountingTime _time;
    private readonly FakeGitHubHandler _h = new();
    private readonly UpdatePaths _paths;
    private readonly UpdateStateStore _store;
    private readonly ActivityHub _hub;
    private readonly BalloonCenter _balloons;
    private readonly Settings _settings = new() { Updates = UpdateMode.Install };
    private readonly List<Balloon> _shown = [];
    private readonly List<StagedUpdate> _installs = [];
    private readonly List<UpdateService> _services = [];
    private readonly List<UpdateClient> _clients = [];

    private volatile bool _busy;
    private string? _exeVersion = "0.0.6";
    private string? _installResult;
    private TaskCompletionSource<string?>? _installGate;

    public UpdateServiceTests()
    {
        _manual.Advance(Now - ManualTimeProvider.T0);
        _time = new CountingTime(_manual);
        _paths = UpdatePaths.Default(_root);
        _store = new UpdateStateStore(_paths.StateFile);
        _hub = new ActivityHub(_manual);
        _balloons = new BalloonCenter(_manual);
        _balloons.Shown += b => _shown.Add(b);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        foreach (var c in _clients) c.Dispose();
        _hub.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private UpdateService Make(bool installed = true, DateTimeOffset? started = null, string running = "0.0.5",
        Func<bool>? isBusy = null, Func<bool>? busyManual = null)
    {
        var client = new UpdateClient(SemVersion.Parse(running), _h, _manual);
        _clients.Add(client);
        var svc = new UpdateService(new UpdateServiceOptions(
            () => _settings, client, _store, _paths, new AppVersionInfo(SemVersion.Parse(running), null),
            installed, Path.Combine(_root, "run"), isBusy ?? (() => _busy), _ => _exeVersion,
            (staged, _) =>
            {
                _installs.Add(staged);
                return _installGate?.Task ?? Task.FromResult(_installResult);
            },
            _hub, _balloons, started ?? Now, _time, IsBusyManual: busyManual));
        _services.Add(svc);
        return svc;
    }

    private byte[] ScriptLatest(string tag = "v0.0.6", int seed = 7,
        Func<int, CancellationToken, Task>? beforeChunk = null, int chunk = 0)
    {
        var exe = Exe(seed);
        _h.On(Api, Reply.Text(ReleaseJson(tag, exe.Length)));
        ScriptDownload(_h, exe, tag, beforeChunk, chunk);
        return exe;
    }

    private List<string> Events() =>
        [.. _hub.GetEvents().Where(e => e.Kind == ActivityKind.Update).Select(e => e.Summary)];

    private int ApiCalls => _h.Count(Api);

    private bool Requested(Uri uri) => _h.Requested(uri.AbsoluteUri);

    private void Seed(Action<UpdateState> change) => _store.Update(change);

    private void SeedStaged(byte[] exe, string version = "0.0.6", bool writeFile = true, int? fileLength = null)
    {
        Seed(s => s.Staged = new StagedUpdate(version, Sha(exe), exe.Length));
        if (!writeFile) return;
        Directory.CreateDirectory(_paths.StagingDir);
        File.WriteAllBytes(_paths.StagedExePath(SemVersion.Parse(version)), exe[..(fileLength ?? exe.Length)]);
    }

    /// <summary>Advances the clock and waits until the service either made its next timer or finished.</summary>
    private async Task Step(Task task, TimeSpan by)
    {
        var before = _time.TimerCount;
        _manual.Advance(by);
        await WaitUntil(() => _time.TimerCount > before || task.IsCompleted);
    }

    // ---- automatic evaluation ----

    [Fact]
    public async Task Not_installed_never_looks_up_or_writes_state()
    {
        var svc = Make(installed: false);
        Assert.False(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_h.Requests);
        Assert.False(File.Exists(_paths.StateFile));
    }

    [Fact]
    public async Task Mode_off_does_nothing()
    {
        _settings.Updates = UpdateMode.Off;
        var svc = Make();
        Assert.False(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_h.Requests);
    }

    [Fact]
    public async Task Not_due_does_nothing()
    {
        Seed(s => s.LastCheckUtc = Now - TimeSpan.FromDays(1));
        var svc = Make();
        Assert.False(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_h.Requests);
    }

    [Fact]
    public async Task Due_and_up_to_date_records_the_check_only()
    {
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.5")));
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        var s = _store.Load();
        Assert.Equal(Now, s.LastCheckUtc);
        Assert.Equal(Now, s.LastAttemptUtc);
        Assert.Null(s.LastError);
        Assert.Equal("v0.0.5", s.LatestTag);
        Assert.Equal(["update check: up to date (v0.0.5)"], Events());
        Assert.Single(_h.Requests);
        Assert.Empty(_shown);
    }

    [Fact]
    public async Task Failed_lookup_retries_after_24_hours()
    {
        _h.On(Api, Reply.Code(500));
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        var s = _store.Load();
        Assert.Equal(Now, s.LastAttemptUtc);
        Assert.Null(s.LastCheckUtc);
        Assert.Equal("GitHub answered HTTP 500", s.LastError);
        Assert.Equal(["update check failed: GitHub answered HTTP 500"], Events());

        _manual.Advance(TimeSpan.FromHours(1));
        Assert.False(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, ApiCalls);

        _manual.Advance(TimeSpan.FromHours(24));
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, ApiCalls);
    }

    [Fact]
    public async Task Notify_mode_posts_one_balloon_per_version()
    {
        _settings.Updates = UpdateMode.Notify;
        ScriptLatest();
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        Assert.Equal(BalloonTexts.UpdateAvailable("0.0.6"), Assert.Single(_shown).Text);
        Assert.Equal("0.0.6", _store.Load().NotifiedVersion);
        Assert.False(Requested(SumsUrl()));
        Assert.False(Requested(ExeUrl()));

        _manual.Advance(TimeSpan.FromDays(7));
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, ApiCalls);
        Assert.Single(_shown);
    }

    [Fact]
    public async Task Notify_mode_keeps_the_version_unnotified_when_the_balloon_is_rate_limited()
    {
        _settings.Updates = UpdateMode.Notify;
        ScriptLatest();
        var svc = Make();
        Assert.True(_balloons.Post("something else"));
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        Assert.Single(_shown);
        Assert.Null(_store.Load().NotifiedVersion);
    }

    [Fact]
    public async Task Install_mode_in_the_startup_window_downloads_and_installs_when_idle()
    {
        var exe = ScriptLatest();
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        var staged = new StagedUpdate("0.0.6", Sha(exe), exe.Length);
        Assert.Equal(staged, _store.Load().Staged);
        Assert.Equal(staged, Assert.Single(_installs));
        Assert.Equal([
            "update check: v0.0.6 available",
            "update v0.0.6 downloaded and verified",
            "installing update v0.0.6",
        ], Events());
        Assert.Equal(UpdateActivity.Installing, svc.Status.Activity);
        Assert.Empty(_shown);
        Assert.True(File.Exists(_paths.StagedExePath(SemVersion.Parse("0.0.6"))));
    }

    [Fact]
    public async Task Install_mode_after_the_window_stages_and_notifies_without_installing()
    {
        ScriptLatest();
        var svc = Make(started: Now - TimeSpan.FromMinutes(31));
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        Assert.Empty(_installs);
        Assert.NotNull(_store.Load().Staged);
        Assert.Equal(BalloonTexts.UpdateReady("0.0.6"), Assert.Single(_shown).Text);
        Assert.Equal(UpdateActivity.None, svc.Status.Activity);
    }

    [Fact]
    public async Task Busy_in_the_startup_window_waits_then_installs_when_idle()
    {
        ScriptLatest();
        _busy = true;
        var svc = Make();
        var task = svc.RunAutomaticAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => svc.Status.Activity == UpdateActivity.WaitingForIdle && _time.TimerCount >= 1);

        for (var i = 0; i < 4; i++) await Step(task, TimeSpan.FromSeconds(5));
        Assert.Empty(_installs);
        Assert.Equal(UpdateActivity.WaitingForIdle, svc.Status.Activity);

        _busy = false;
        await Step(task, TimeSpan.FromSeconds(5));
        Assert.True(await task);
        Assert.Single(_installs);
        Assert.Equal(UpdateActivity.Installing, svc.Status.Activity);
    }

    [Fact]
    public async Task Busy_for_the_whole_startup_wait_gives_up_and_notifies()
    {
        ScriptLatest();
        _busy = true;
        var svc = Make();
        var task = svc.RunAutomaticAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => svc.Status.Activity == UpdateActivity.WaitingForIdle && _time.TimerCount >= 1);

        var steps = 0;
        while (!task.IsCompleted && steps++ < 200) await Step(task, TimeSpan.FromSeconds(5));

        Assert.True(await task);
        Assert.Empty(_installs);
        Assert.Equal(BalloonTexts.UpdateReady("0.0.6"), Assert.Single(_shown).Text);
        Assert.Equal(UpdateActivity.None, svc.Status.Activity);
        Assert.NotNull(_store.Load().Staged);
        Assert.Equal(120, steps);
    }

    [Fact]
    public async Task A_failed_install_is_recorded_and_the_staged_file_is_kept()
    {
        ScriptLatest();
        _installResult = UpdateTexts.ReasonOldInUse;
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        var s = _store.Load();
        Assert.Equal(UpdateTexts.ReasonOldInUse, s.LastError);
        Assert.NotNull(s.Staged);
        Assert.Contains("update failed: the previous version is still in use; restart Windows and try again", Events());
        Assert.Equal(UpdateActivity.None, svc.Status.Activity);
        Assert.True(File.Exists(_paths.StagedExePath(SemVersion.Parse("0.0.6"))));
    }

    [Fact]
    public async Task A_downloaded_exe_with_the_wrong_version_is_deleted()
    {
        ScriptLatest();
        _exeVersion = "0.0.7";
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        var s = _store.Load();
        Assert.Null(s.Staged);
        Assert.Equal(UpdateTexts.ReasonVersionMismatch, s.LastError);
        Assert.False(File.Exists(_paths.StagedExePath(SemVersion.Parse("0.0.6"))));
        Assert.Contains("update download failed: " + UpdateTexts.ReasonVersionMismatch, Events());
        Assert.Empty(_installs);
    }

    [Fact]
    public async Task The_failed_version_is_skipped_but_a_newer_one_is_taken()
    {
        Seed(s => s.FailedVersion = "0.0.6");
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.6", Exe(9).Length)));
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.7", Exe(9).Length)));
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.False(Requested(SumsUrl()));
        Assert.Empty(_shown);
        Assert.Empty(_installs);

        _manual.Advance(TimeSpan.FromDays(8));
        ScriptDownload(_h, Exe(9), "v0.0.7");
        _exeVersion = "0.0.7";
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Equal("0.0.7", _store.Load().Staged?.Version);
    }

    [Fact]
    public async Task A_valid_staged_file_is_reused_without_downloading()
    {
        var exe = Exe();
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.6", exe.Length)));
        SeedStaged(exe);
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        Assert.Single(_h.Requests);
        Assert.Equal(new StagedUpdate("0.0.6", Sha(exe), exe.Length), Assert.Single(_installs));
    }

    [Fact]
    public async Task A_staged_file_of_the_wrong_size_is_downloaded_again()
    {
        var exe = ScriptLatest();
        SeedStaged(exe, fileLength: 5_000);
        var svc = Make();
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        Assert.True(Requested(ExeUrl()));
        Assert.Equal(exe, await File.ReadAllBytesAsync(_paths.StagedExePath(SemVersion.Parse("0.0.6")), TestContext.Current.CancellationToken));
        Assert.Single(_installs);
    }

    [Fact]
    public async Task When_nothing_is_newer_the_staged_update_is_dropped_but_other_files_stay()
    {
        var exe = Exe();
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.6", exe.Length)));
        SeedStaged(exe);
        var notes = Path.Combine(_paths.StagingDir, "notes.txt");
        var other = Path.Combine(_paths.StagingDir, "ClippysRevenge-0.0.4.exe");
        var partial = Path.Combine(_paths.StagingDir, "ClippysRevenge-0.0.9.exe.partial");
        File.WriteAllText(notes, "keep me");
        File.WriteAllText(other, "x");
        File.WriteAllText(partial, "x");
        var svc = Make(running: "0.0.6");
        Assert.True(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));

        Assert.Null(_store.Load().Staged);
        Assert.False(File.Exists(_paths.StagedExePath(SemVersion.Parse("0.0.6"))));
        Assert.False(File.Exists(other));
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(notes));
        Assert.Empty(_installs);
    }

    // ---- manual actions ----

    [Fact]
    public async Task Check_now_looks_up_whatever_the_mode_and_never_downloads()
    {
        _settings.Updates = UpdateMode.Off;
        Seed(s => s.LastCheckUtc = Now - TimeSpan.FromHours(1));
        ScriptLatest();
        var svc = Make();
        await svc.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, ApiCalls);
        Assert.Equal("v0.0.6", _store.Load().LatestTag);
        Assert.Equal(Now, _store.Load().LastCheckUtc);
        Assert.False(Requested(SumsUrl()));
        Assert.Empty(_shown);
        Assert.Null(svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Check_now_failure_sets_the_action_message()
    {
        _h.On(Api, Reply.Code(500));
        var svc = Make();
        await svc.CheckNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal("✗ Couldn't check for updates: GitHub answered HTTP 500", svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Check_now_works_when_not_installed_and_writes_no_state()
    {
        ScriptLatest();
        var svc = Make(installed: false);
        await svc.CheckNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal("v0.0.6", svc.Status.LatestTag);
        Assert.Empty(Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task Update_now_without_a_known_latest_looks_up_downloads_and_installs()
    {
        var exe = ScriptLatest();
        var svc = Make();
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new StagedUpdate("0.0.6", Sha(exe), exe.Length), Assert.Single(_installs));
        Assert.Equal(1, ApiCalls);
        Assert.Null(svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Update_now_does_nothing_when_already_current()
    {
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.5")));
        var svc = Make();
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_installs);
        Assert.Single(_h.Requests);
        Assert.Null(svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Update_now_ignores_the_failed_version_marker()
    {
        Seed(s => s.FailedVersion = "0.0.6");
        ScriptLatest();
        var svc = Make();
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        Assert.Single(_installs);
    }

    [Fact]
    public async Task Update_now_reports_busy_after_two_minutes()
    {
        ScriptLatest();
        _busy = true;
        var svc = Make();
        var task = svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => svc.Status.Activity == UpdateActivity.WaitingForIdle && _time.TimerCount >= 1);

        var steps = 0;
        while (!task.IsCompleted && steps++ < 200) await Step(task, TimeSpan.FromSeconds(5));
        await task;

        Assert.Equal(UpdateTexts.Busy, svc.Status.ActionMessage);
        Assert.Empty(_installs);
        Assert.Equal(UpdateActivity.None, svc.Status.Activity);
    }

    [Fact]
    public async Task Update_now_ignores_the_settings_window_but_an_automatic_install_does_not()
    {
        ScriptLatest();
        _busy = true;   // only Settings is open: IsBusy true, the manual test false
        var svc = Make(busyManual: () => false);

        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);

        Assert.Single(_installs);
        Assert.Null(svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Automatic_install_rechecks_busy_right_before_requesting()
    {
        ScriptLatest();
        var calls = 0;
        var installingAtRecheck = -1;
        var svc = Make(isBusy: () =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 2)
            {
                installingAtRecheck = Events().Count(e => e.StartsWith("installing update", StringComparison.Ordinal));
            }
            return n == 2;
        });
        var task = svc.RunAutomaticAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => task.IsCompleted || _time.TimerCount >= 1);

        while (!task.IsCompleted) await Step(task, TimeSpan.FromSeconds(5));

        Assert.True(await task);
        Assert.Equal(0, installingAtRecheck);
        Assert.Single(_installs);
        Assert.Single(Events(), e => e.StartsWith("installing update", StringComparison.Ordinal));
        Assert.True(Volatile.Read(ref calls) >= 4, "the idle test must run again before the install");
    }

    [Fact]
    public async Task Stop_waits_for_a_swap_in_progress()
    {
        ScriptLatest();
        _installGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = Make();
        var update = svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => _installs.Count == 1);

        var stop = svc.StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(stop.IsCompleted);

        _installGate.SetResult(null);
        await stop;
        await update;
    }

    [Fact]
    public async Task Update_now_reports_a_failed_install()
    {
        ScriptLatest();
        _installResult = UpdateTexts.ReasonOldInUse;
        var svc = Make();
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal("✗ The update couldn't be installed: " + UpdateTexts.ReasonOldInUse, svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Update_now_reports_a_failed_download()
    {
        ScriptLatest();
        _exeVersion = "9.9.9";
        var svc = Make();
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal("✗ The update couldn't be downloaded: " + UpdateTexts.ReasonVersionMismatch,
            svc.Status.ActionMessage);
    }

    [Fact]
    public async Task Update_now_when_not_installed_refuses()
    {
        var svc = Make(installed: false);
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        Assert.Equal("✗ The update couldn't be installed: this copy isn't in the install folder",
            svc.Status.ActionMessage);
        Assert.Empty(_h.Requests);
        Assert.Empty(_installs);
    }

    [Fact]
    public async Task Another_operation_makes_manual_calls_return_at_once_and_progress_is_visible()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptLatest(chunk: 262_144, beforeChunk: async (i, ct) =>
        {
            if (i == 2) await gate.Task.WaitAsync(ct);
        });
        var svc = Make();
        var changed = 0;
        svc.Changed += () => Interlocked.Increment(ref changed);

        var update = svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        await WaitUntil(() => svc.Status.Activity == UpdateActivity.Downloading && svc.Status.Percent >= 40);
        Assert.Equal("0.0.6", svc.Status.ActivityVersion);
        var calls = ApiCalls;

        await svc.CheckNowAsync(TestContext.Current.CancellationToken);
        await svc.UpdateNowAsync(TestContext.Current.CancellationToken);
        Assert.False(await svc.RunAutomaticAsync(TestContext.Current.CancellationToken));
        Assert.Equal(calls, ApiCalls);
        Assert.True(changed > 0);

        gate.SetResult();
        await update;
        Assert.Single(_installs);
    }

    // ---- ExecuteAsync ----

    [Fact]
    public async Task The_loop_waits_60_seconds_then_evaluates_every_6_hours()
    {
        _settings.Updates = UpdateMode.Notify;
        _h.On(Api, Reply.Text(ReleaseJson("v0.0.5")));
        var svc = Make();
        await svc.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntil(() => _time.TimerCount >= 1);
            _manual.Advance(TimeSpan.FromSeconds(59));
            Assert.Empty(_h.Requests);

            _manual.Advance(TimeSpan.FromSeconds(1));
            await WaitUntil(() => _h.Requests.Count == 1 && _time.TimerCount >= 2);

            _manual.Advance(UpdatePolicy.EvaluateInterval);
            await WaitUntil(() => _time.TimerCount >= 3);
            Assert.Single(_h.Requests);
        }
        finally
        {
            await svc.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    // ---- status ----

    [Fact]
    public void Status_maps_the_state()
    {
        var exe = Exe();
        var published = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        Seed(s =>
        {
            s.LastCheckUtc = Now - TimeSpan.FromHours(2);
            s.LastError = "boom";
            s.LatestTag = "v0.0.6";
            s.LatestPublishedUtc = published;
            s.Staged = new StagedUpdate("0.0.6", Sha(exe), exe.Length);
            s.FailedVersion = "0.0.4";
        });
        _settings.Updates = UpdateMode.Notify;
        var svc = Make();
        var st = svc.Status;

        Assert.Equal(V005, st.Running.Version);
        Assert.True(st.Installed);
        Assert.Equal(Path.Combine(_root, "run"), st.RunningDir);
        Assert.Equal(UpdateMode.Notify, st.Mode);
        Assert.Equal(Now - TimeSpan.FromHours(2), st.LastCheckUtc);
        Assert.Equal("boom", st.LastError);
        Assert.Equal("v0.0.6", st.LatestTag);
        Assert.Equal(published, st.LatestPublishedUtc);
        Assert.Equal("0.0.6", st.StagedVersion);
        Assert.Equal("0.0.4", st.FailedVersion);
        Assert.Equal(UpdateActivity.None, st.Activity);
        Assert.Equal(0, st.Percent);
        Assert.Null(st.ActivityVersion);
        Assert.Null(st.ActionMessage);
    }

    [Fact]
    public void About_shows_the_ready_line_for_a_staged_update()
    {
        var exe = Exe();
        Seed(s =>
        {
            s.LastCheckUtc = Now;
            s.LatestTag = "v0.0.6";
            s.Staged = new StagedUpdate("0.0.6", Sha(exe), exe.Length);
        });
        var svc = Make();
        var model = AboutView.Build(svc.Status, ".NET 10", "Windows", Now, TimeZoneInfo.Utc);
        Assert.Equal(UpdateTexts.Ready("0.0.6"), model.StatusLine);
        Assert.True(model.UpdateNowEnabled);
    }

    [Fact]
    public void Constructor_rejects_null_options()
    {
        Assert.Throws<ArgumentNullException>(() => new UpdateService(null!));
    }
}
