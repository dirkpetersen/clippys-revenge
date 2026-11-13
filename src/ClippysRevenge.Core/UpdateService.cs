using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <param name="Installed">UpdatePaths.IsInstalledCopy(Environment.ProcessPath), decided once by Program.</param>
/// <param name="RunningDir">Folder of the running exe (About's location line).</param>
/// <param name="IsBusy">SPEC 20.9 idle test (hub scopes + Settings window). Any thread, must not block.</param>
/// <param name="ReadExeVersion">ProductVersion cut at '+', or null (App: UpdateRestarter.ReadExeVersion).</param>
/// <param name="RequestInstall">Swaps the files and asks the app to exit for the handover. Returns null when the app
/// is now exiting, else an UpdateTexts reason (the old exe is still in place). Never throws for expected failures.</param>
/// <param name="StartedUtc">Process start (for the 30-minute start-up window).</param>
/// <param name="IsBusyManual">Idle test for a user-initiated Update now: like IsBusy but without the Settings window
/// (the user is looking at it, e.g. About opened from Settings). Null = use IsBusy.</param>
public sealed record UpdateServiceOptions(
    Func<Settings> Settings,
    UpdateClient Client,
    UpdateStateStore Store,
    UpdatePaths Paths,
    AppVersionInfo Running,
    bool Installed,
    string RunningDir,
    Func<bool> IsBusy,
    Func<string, string?> ReadExeVersion,
    Func<StagedUpdate, CancellationToken, Task<string?>> RequestInstall,
    ActivityHub Hub,
    BalloonCenter Balloons,
    DateTimeOffset StartedUtc,
    TimeProvider? Time = null,
    ILogger? Logger = null,
    Func<bool>? IsBusyManual = null);

/// <summary>The update schedule (SPEC 20.8): weekly check, notify or stage, install in the start-up window, and the
/// manual actions of the About window.</summary>
public sealed class UpdateService : BackgroundService
{
    private readonly UpdateServiceOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    /// <summary>How long StopAsync waits for a file swap that is in progress.</summary>
    public static readonly TimeSpan InstallStopWait = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _op = new(1, 1);
    private readonly Lock _lock = new();

    private UpdateState _state;
    private UpdateActivity _activity = UpdateActivity.None;
    private int _percent;
    private string? _activityVersion;
    private string? _actionMessage;
    private ReleaseInfo? _lastRelease;
    private Task? _installTask;

    /// <exception cref="ArgumentNullException">options or a non-nullable member is null.</exception>
    public UpdateService(UpdateServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Settings);
        ArgumentNullException.ThrowIfNull(options.Client);
        ArgumentNullException.ThrowIfNull(options.Store);
        ArgumentNullException.ThrowIfNull(options.Paths);
        ArgumentNullException.ThrowIfNull(options.Running);
        ArgumentNullException.ThrowIfNull(options.RunningDir);
        ArgumentNullException.ThrowIfNull(options.IsBusy);
        ArgumentNullException.ThrowIfNull(options.ReadExeVersion);
        ArgumentNullException.ThrowIfNull(options.RequestInstall);
        ArgumentNullException.ThrowIfNull(options.Hub);
        ArgumentNullException.ThrowIfNull(options.Balloons);
        _o = options;
        _time = options.Time ?? TimeProvider.System;
        _log = options.Logger;
        // Not installed: the store is never read or written (SPEC 20.6).
        _state = options.Installed ? options.Store.Load() : new UpdateState();
    }

    /// <summary>Raised after every Status change, on the thread that made it. Handlers must not block; exceptions
    /// are caught and logged (type only).</summary>
    public event Action? Changed;

    /// <summary>A consistent snapshot. Any thread.</summary>
    public UpdateStatus Status
    {
        get
        {
            var mode = _o.Settings().Updates;
            lock (_lock)
            {
                return new UpdateStatus(_o.Running, _o.Installed, _o.RunningDir, mode,
                    _state.LastCheckUtc, _state.LastError, _state.LatestTag, _state.LatestPublishedUtc,
                    _state.Staged?.Version, _state.FailedVersion,
                    _activity, _percent, _activityVersion, _actionMessage);
            }
        }
    }

    private SemVersion Running => _o.Running.Version;

    // ---- schedule ----

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(UpdatePolicy.FirstCheckDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunAutomaticAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log?.LogError("Automatic update evaluation failed ({Type})", ex.GetType().Name);
                }
                await Task.Delay(UpdatePolicy.EvaluateInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One automatic evaluation (SPEC 20.8). True when it looked up GitHub. Exposed for tests.</summary>
    internal async Task<bool> RunAutomaticAsync(CancellationToken ct)
    {
        if (!_o.Installed || _o.Settings().Updates == UpdateMode.Off)
        {
            return false;
        }
        if (!_op.Wait(0))
        {
            return false;
        }
        try
        {
            bool due;
            lock (_lock)
            {
                _state = _o.Store.Load();
                due = UpdatePolicy.IsDue(_state, _time.GetUtcNow());
            }
            if (!due)
            {
                return false;
            }

            var r = await CheckCoreAsync(ct).ConfigureAwait(false);
            if (r.Outcome != UpdateCheckOutcome.Ok || r.Release is not { } latest)
            {
                return true;
            }

            string? failed;
            lock (_lock) failed = _state.FailedVersion;
            if (!UpdatePolicy.IsUpdate(latest.Version, Running, failed))
            {
                DropStaged(null);
                return true;
            }

            var v = latest.Version.ToString();
            switch (_o.Settings().Updates)
            {
                case UpdateMode.Notify:
                    NotifyOnce(latest.Version, BalloonTexts.UpdateAvailable(v));
                    break;
                case UpdateMode.Install:
                    var staged = ReusableStaged(latest.Version) ?? await DownloadCoreAsync(latest, ct).ConfigureAwait(false);
                    if (staged is null)
                    {
                        break;
                    }
                    if (UpdatePolicy.InStartupWindow(_o.StartedUtc, _time.GetUtcNow())
                        && await InstallWhenIdleAsync(staged, UpdatePolicy.StartupIdleWait, manual: false, ct)
                            .ConfigureAwait(false))
                    {
                        return true;
                    }
                    NotifyOnce(latest.Version, BalloonTexts.UpdateReady(v));
                    break;
            }
            return true;
        }
        finally
        {
            _op.Release();
        }
    }

    // ---- manual actions (SPEC 20.8) ----

    /// <summary>Manual "Check for updates": one lookup, whatever the mode and due time; no download, no balloon.
    /// No-op while another operation runs. Never throws except OperationCanceledException for ct.</summary>
    public async Task CheckNowAsync(CancellationToken ct)
    {
        if (!_op.Wait(0))
        {
            return;
        }
        try
        {
            SetMessage(null);
            var r = await CheckCoreAsync(ct).ConfigureAwait(false);
            if (r.Outcome == UpdateCheckOutcome.Failed)
            {
                SetMessage(UpdateTexts.CheckFailed(r.Error ?? ""));
            }
        }
        finally
        {
            _op.Release();
        }
    }

    /// <summary>Manual "Update now". No-op while another operation runs. Never throws except
    /// OperationCanceledException for ct.</summary>
    public async Task UpdateNowAsync(CancellationToken ct)
    {
        if (!_o.Installed)
        {
            SetMessage(UpdateTexts.InstallFailed(UpdateTexts.ReasonNotInstalled));
            return;
        }
        if (!_op.Wait(0))
        {
            return;
        }
        try
        {
            SetMessage(null);

            var latest = LatestVersion();
            if (latest is null)
            {
                var r = await CheckCoreAsync(ct).ConfigureAwait(false);
                if (r.Outcome == UpdateCheckOutcome.Failed)
                {
                    SetMessage(UpdateTexts.CheckFailed(r.Error ?? ""));
                    return;
                }
                latest = LatestVersion();
            }
            if (latest is null || !UpdatePolicy.IsNewer(latest, Running))
            {
                return;
            }

            var staged = ReusableStaged(latest);
            if (staged is null)
            {
                ReleaseInfo? release;
                lock (_lock) release = _lastRelease;
                if (release is null || release.Version != latest)
                {
                    var r = await CheckCoreAsync(ct).ConfigureAwait(false);
                    if (r.Outcome == UpdateCheckOutcome.Failed)
                    {
                        SetMessage(UpdateTexts.CheckFailed(r.Error ?? ""));
                        return;
                    }
                    release = r.Release;
                    if (release is null || !UpdatePolicy.IsNewer(release.Version, Running))
                    {
                        return;
                    }
                }
                staged = await DownloadCoreAsync(release, ct).ConfigureAwait(false);
                if (staged is null)
                {
                    string? reason;
                    lock (_lock) reason = _state.LastError;
                    SetMessage(UpdateTexts.DownloadFailed(reason ?? ""));
                    return;
                }
            }

            await InstallWhenIdleAsync(staged, UpdatePolicy.ManualIdleWait, manual: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _op.Release();
        }
    }

    // ---- building blocks ----

    /// <summary>Waits (up to 30 s) for an install that is swapping files, so the process never exits with only the
    /// .old exe on disk.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Task? install;
        lock (_lock) install = _installTask;
        if (install is null)
        {
            return;
        }
        try
        {
            await install.WaitAsync(InstallStopWait, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _log?.LogWarning("The update install was still running at exit.");
        }
        catch (Exception)
        {
            // The install's own failure is reported through its caller.
        }
    }

    private SemVersion? LatestVersion()
    {
        lock (_lock) return _state.LatestVersion;
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken ct)
    {
        SetActivity(UpdateActivity.Checking);
        try
        {
            var r = await _o.Client.GetLatestAsync(ct).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            switch (r.Outcome)
            {
                case UpdateCheckOutcome.Ok when r.Release is { } release:
                    lock (_lock) _lastRelease = release;
                    Mutate(s =>
                    {
                        s.LastCheckUtc = now;
                        s.LastAttemptUtc = now;
                        s.LastError = null;
                        s.LatestTag = release.Tag;
                        s.LatestPublishedUtc = release.PublishedUtc;
                    });
                    _o.Hub.Event(ActivityKind.Update, UpdatePolicy.IsNewer(release.Version, Running)
                        ? UpdateTexts.EventAvailable(release.Version.ToString())
                        : UpdateTexts.EventUpToDate(Running.ToString()));
                    break;
                case UpdateCheckOutcome.NoRelease:
                    lock (_lock) _lastRelease = null;
                    Mutate(s =>
                    {
                        s.LastCheckUtc = now;
                        s.LastAttemptUtc = now;
                        s.LastError = null;
                        s.LatestTag = null;
                        s.LatestPublishedUtc = null;
                    });
                    _o.Hub.Event(ActivityKind.Update, UpdateTexts.EventUpToDate(Running.ToString()));
                    break;
                default:
                    var error = r.Error ?? UpdateTexts.ReasonBadJson;
                    Mutate(s =>
                    {
                        s.LastAttemptUtc = now;
                        s.LastError = error;
                    });
                    _o.Hub.Event(ActivityKind.Update, UpdateTexts.EventCheckFailed(error));
                    _log?.LogWarning("Update check failed: {Reason}", error);
                    break;
            }
            return r;
        }
        finally
        {
            SetActivity(UpdateActivity.None);
        }
    }

    private async Task<StagedUpdate?> DownloadCoreAsync(ReleaseInfo release, CancellationToken ct)
    {
        var v = release.Version;
        SetActivity(UpdateActivity.Downloading, v.ToString());
        try
        {
            string? reason = null;
            StagedDownload? d = null;
            try
            {
                d = await _o.Client.DownloadAsync(release, _o.Paths.StagingDir, new PercentProgress(this), ct)
                    .ConfigureAwait(false);
            }
            catch (UpdateDownloadException ex)
            {
                reason = ex.Reason;
            }

            if (d is not null)
            {
                SetActivity(UpdateActivity.Verifying, v.ToString());
                string? version;
                try
                {
                    version = _o.ReadExeVersion(d.Path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    version = null;
                }
                if (!string.Equals(version, v.ToString(), StringComparison.Ordinal))
                {
                    TryDelete(d.Path);
                    reason = UpdateTexts.ReasonVersionMismatch;
                    d = null;
                }
            }

            if (d is null)
            {
                var failure = reason ?? UpdateTexts.ReasonVersionMismatch;
                Mutate(s => s.LastError = failure);
                _o.Hub.Event(ActivityKind.Update, UpdateTexts.EventDownloadFailed(failure));
                _log?.LogWarning("Update {Version} download failed: {Reason}", v, failure);
                return null;
            }

            var staged = d.ToStaged();
            Mutate(s =>
            {
                s.Staged = staged;
                s.LastError = null;
            });
            DropStaged(v);
            _o.Hub.Event(ActivityKind.Update, UpdateTexts.EventStaged(v.ToString()));
            _log?.LogInformation("Update {Version} downloaded and verified ({Bytes} bytes)", v, staged.Size);
            return staged;
        }
        finally
        {
            SetActivity(UpdateActivity.None);
        }
    }

    private StagedUpdate? ReusableStaged(SemVersion v)
    {
        StagedUpdate? staged;
        lock (_lock) staged = _state.Staged;
        if (staged is null || !string.Equals(staged.Version, v.ToString(), StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            var info = new FileInfo(_o.Paths.StagedExePath(v));
            return info.Exists && info.Length == staged.Size ? staged : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DropStaged(SemVersion? keep)
    {
        bool clear;
        lock (_lock)
        {
            clear = _state.Staged is { } st
                && (keep is null || !string.Equals(st.Version, keep.ToString(), StringComparison.Ordinal));
        }
        if (clear)
        {
            Mutate(s => s.Staged = null);
        }

        var dir = _o.Paths.StagingDir;
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(path);
                var drop = name.EndsWith(UpdatePaths.PartialSuffix, StringComparison.OrdinalIgnoreCase)
                    || (UpdatePaths.TryParseStagedFileName(name, out var version) && (keep is null || version != keep));
                if (drop)
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogDebug("Couldn't clean the staging folder ({Type})", ex.GetType().Name);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogDebug("Couldn't delete a staged file ({Type})", ex.GetType().Name);
        }
    }

    private async Task<bool> InstallWhenIdleAsync(StagedUpdate staged, TimeSpan maxWait, bool manual,
        CancellationToken ct)
    {
        // A user-initiated install ignores the Settings window; an automatic one must not interrupt it.
        var isBusy = manual && _o.IsBusyManual is { } busyManual ? busyManual : _o.IsBusy;
        var start = _time.GetUtcNow();
        while (true)
        {
            if (isBusy())
            {
                SetActivity(UpdateActivity.WaitingForIdle);
                while (isBusy())
                {
                    if (_time.GetUtcNow() - start >= maxWait)
                    {
                        SetActivity(UpdateActivity.None);
                        if (manual)
                        {
                            SetMessage(UpdateTexts.Busy);
                        }
                        return false;
                    }
                    try
                    {
                        await Task.Delay(UpdatePolicy.IdlePoll, _time, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        SetActivity(UpdateActivity.None);
                        throw;
                    }
                }
            }

            SetActivity(UpdateActivity.Installing, staged.Version);
            _o.Hub.Event(ActivityKind.Update, UpdateTexts.EventInstalling(staged.Version));
            _log?.LogInformation("Installing update {Version}", staged.Version);

            // Re-check right before the install: the user may have become busy meanwhile (automatic installs only).
            if (manual || !isBusy())
            {
                break;
            }
        }

        string? err;
        try
        {
            var install = _o.RequestInstall(staged, ct);
            lock (_lock) _installTask = install;
            try
            {
                err = await install.ConfigureAwait(false);
            }
            finally
            {
                lock (_lock) _installTask = null;
            }
        }
        catch (OperationCanceledException)
        {
            SetActivity(UpdateActivity.None);
            throw;
        }
        catch (Exception ex)
        {
            err = UpdateTexts.ReasonReplaceFailed(ex.GetType().Name);
        }
        if (err is null)
        {
            return true; // The app is exiting; Activity stays Installing.
        }

        Mutate(s => s.LastError = err);
        _o.Hub.Event(ActivityKind.Update, UpdateTexts.EventInstallFailed(err));
        _log?.LogWarning("Update {Version} install failed: {Reason}", staged.Version, err);
        SetActivity(UpdateActivity.None);
        if (manual)
        {
            SetMessage(UpdateTexts.InstallFailed(err));
        }
        return false;
    }

    private void NotifyOnce(SemVersion v, string text)
    {
        var versionText = v.ToString();
        lock (_lock)
        {
            if (string.Equals(_state.NotifiedVersion, versionText, StringComparison.Ordinal))
            {
                return;
            }
        }
        if (_o.Balloons.Post(text))
        {
            Mutate(s => s.NotifiedVersion = versionText);
        }
    }

    // ---- state and status ----

    private void Mutate(Action<UpdateState> change)
    {
        lock (_lock)
        {
            var saved = false;
            if (_o.Installed)
            {
                try
                {
                    _state = _o.Store.Update(change);
                    saved = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log?.LogWarning("update-state.json could not be written ({Type}); keeping the change in memory.",
                        ex.GetType().Name);
                }
            }
            if (!saved)
            {
                var copy = _state.Clone();
                change(copy);
                _state = copy;
            }
        }
        RaiseChanged();
    }

    private void SetActivity(UpdateActivity activity, string? version = null)
    {
        lock (_lock)
        {
            _activity = activity;
            if (activity == UpdateActivity.None)
            {
                _activityVersion = null;
                _percent = 0;
            }
            else if (version is not null)
            {
                _activityVersion = version;
                if (activity is UpdateActivity.Downloading)
                {
                    _percent = 0;
                }
            }
        }
        RaiseChanged();
    }

    private void SetMessage(string? message)
    {
        lock (_lock) _actionMessage = message;
        RaiseChanged();
    }

    private void SetPercent(int percent)
    {
        lock (_lock)
        {
            if (_percent == percent)
            {
                return;
            }
            _percent = percent;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null)
        {
            return;
        }
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception ex)
            {
                _log?.LogWarning("An update status handler failed ({Type})", ex.GetType().Name);
            }
        }
    }

    private sealed class PercentProgress(UpdateService owner) : IProgress<int>
    {
        public void Report(int value) => owner.SetPercent(value);
    }
}
