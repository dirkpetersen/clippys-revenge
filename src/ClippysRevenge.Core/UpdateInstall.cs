using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>The process started for a handover. The App wraps System.Diagnostics.Process (M12.4).</summary>
public interface IUpdateProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Kills this process only (not its tree). May throw if it already exited; callers catch.</summary>
    void Kill();

    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>Message = an UpdateTexts reason (SPEC 20.10). The old exe is in place when this is thrown.</summary>
public sealed class UpdateInstallException : Exception
{
    public UpdateInstallException(string reason, Exception? inner = null) : base(reason, inner)
    {
    }
}

internal static class UpdateFiles
{
    /// <summary>Runs op; retries IOException and UnauthorizedAccessException (not file/directory not found) every
    /// <paramref name="every"/> until <paramref name="retryFor"/> has passed since the first try, then rethrows.</summary>
    public static async Task RetryAsync(Action op, TimeSpan retryFor, TimeSpan every, TimeProvider time,
        CancellationToken ct)
    {
        var start = time.GetUtcNow();
        while (true)
        {
            try
            {
                op();
                return;
            }
            catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException)
                and not (FileNotFoundException or DirectoryNotFoundException))
            {
                if (time.GetUtcNow() - start >= retryFor)
                {
                    throw;
                }
            }
            await Task.Delay(every, time, ct).ConfigureAwait(false);
        }
    }

    public static bool IsIo(Exception ex) => ex is IOException or UnauthorizedAccessException;
}

public static class UpdateInstall
{
    public static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>SPEC 20.9 "Swap": verify size + SHA-256, delete a leftover .old, rename exe → .old, move the staged
    /// file → exe (restore on failure), then Pending = {running, staged, now, false} and Staged = null.</summary>
    /// <exception cref="UpdateInstallException">Any failure; the installed exe is unchanged.</exception>
    /// <exception cref="OperationCanceledException">ct was cancelled before the first rename.</exception>
    public static async Task<PendingInstall> BeginAsync(UpdatePaths paths, UpdateStateStore store, StagedUpdate staged,
        SemVersion running, TimeProvider? time = null, ILogger? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(running);
        time ??= TimeProvider.System;
        ct.ThrowIfCancellationRequested();

        if (!SemVersion.TryParse(staged.Version, out var v))
        {
            throw new UpdateInstallException(UpdateTexts.ReasonStagedMissing);
        }
        var src = paths.StagedExePath(v);
        if (!File.Exists(src))
        {
            ClearStaged(store, log);
            throw new UpdateInstallException(UpdateTexts.ReasonStagedMissing);
        }

        long length;
        try
        {
            length = new FileInfo(src).Length;
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            throw new UpdateInstallException(UpdateTexts.ReasonReplaceFailed(ex.GetType().Name), ex);
        }
        if (length != staged.Size)
        {
            DeleteQuietly(src, log);
            ClearStaged(store, log);
            throw new UpdateInstallException(UpdateTexts.ReasonSizeMismatch);
        }

        string actual;
        try
        {
            actual = await UpdateHash.OfFileAsync(src, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            throw new UpdateInstallException(UpdateTexts.ReasonReplaceFailed(ex.GetType().Name), ex);
        }
        if (!UpdateHash.Same(actual, staged.Sha256))
        {
            DeleteQuietly(src, log);
            ClearStaged(store, log);
            throw new UpdateInstallException(UpdateTexts.ReasonHashMismatch);
        }

        if (!File.Exists(paths.ExePath))
        {
            throw new UpdateInstallException(UpdateTexts.ReasonReplaceFailed(nameof(FileNotFoundException)));
        }

        if (File.Exists(paths.OldExePath))
        {
            try
            {
                await UpdateFiles.RetryAsync(() => File.Delete(paths.OldExePath), RetryFor, RetryEvery, time, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (UpdateFiles.IsIo(ex))
            {
                throw new UpdateInstallException(UpdateTexts.ReasonOldInUse, ex);
            }
        }

        try
        {
            await UpdateFiles.RetryAsync(() => File.Move(paths.ExePath, paths.OldExePath), RetryFor, RetryEvery,
                time, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            throw new UpdateInstallException(UpdateTexts.ReasonReplaceFailed(ex.GetType().Name), ex);
        }

        // From here on cancellation is ignored so a half-done swap is never left behind.
        try
        {
            await UpdateFiles.RetryAsync(() => File.Move(src, paths.ExePath), RetryFor, RetryEvery, time,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            try
            {
                await UpdateFiles.RetryAsync(() => File.Move(paths.OldExePath, paths.ExePath), RetryFor, RetryEvery,
                    time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception restoreEx) when (UpdateFiles.IsIo(restoreEx))
            {
                log?.LogCritical(
                    "Update swap failed and the old exe could not be restored; run install.ps1 to repair.");
            }
            throw new UpdateInstallException(UpdateTexts.ReasonReplaceFailed(ex.GetType().Name), ex);
        }

        var pending = new PendingInstall(running.ToString(), v.ToString(), time.GetUtcNow(), false);
        try
        {
            store.Update(s =>
            {
                s.Pending = pending;
                s.Staged = null;
                s.LastError = null;
            });
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogWarning("Update swap: the state file couldn't be written ({Type}).", ex.GetType().Name);
        }
        log?.LogInformation("Update swap: {From} -> {To}", pending.FromVersion, pending.ToVersion);
        return pending;
    }

    private static void ClearStaged(UpdateStateStore store, ILogger? log)
    {
        try
        {
            store.Update(s => s.Staged = null);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogWarning("Update state couldn't be written ({Type}).", ex.GetType().Name);
        }
    }

    private static void DeleteQuietly(string path, ILogger? log)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogInformation("A staged file couldn't be deleted ({Type}).", ex.GetType().Name);
        }
    }
}

public enum HandoverOutcome { Confirmed, RolledBack, RollbackFailed }

public static class UpdateHandover
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RestoreRetryFor = TimeSpan.FromSeconds(10);

    /// <summary>SPEC 20.9 "Handover" and "Rollback". Never throws except OperationCanceledException for ct.</summary>
    /// <param name="start">Starts the exe at the given path. Called only with paths.ExePath.</param>
    public static async Task<HandoverOutcome> RunAsync(UpdatePaths paths, UpdateStateStore store,
        PendingInstall pending, Func<string, IUpdateProcess> start, TimeProvider? time = null, ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(start);
        time ??= TimeProvider.System;

        IUpdateProcess p;
        try
        {
            p = start(paths.ExePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogError("Update handover: the new version couldn't be started ({Type}).", ex.GetType().Name);
            return await RollbackAsync(paths, store, pending, start, null, time, log, ct).ConfigureAwait(false);
        }

        var deadline = time.GetUtcNow() + ConfirmTimeout;
        while (true)
        {
            if (store.TryLoad(out var s) && IsConfirmed(s, pending))
            {
                p.Dispose();
                log?.LogInformation("Update handover: {To} confirmed", pending.ToVersion);
                return HandoverOutcome.Confirmed;
            }
            if (HasExited(p))
            {
                log?.LogWarning("Update handover: {To} exited before confirming", pending.ToVersion);
                return await RollbackAsync(paths, store, pending, start, p, time, log, ct).ConfigureAwait(false);
            }
            if (time.GetUtcNow() >= deadline)
            {
                log?.LogWarning("Update handover: {To} did not confirm within 60 s", pending.ToVersion);
                return await RollbackAsync(paths, store, pending, start, p, time, log, ct).ConfigureAwait(false);
            }
            try
            {
                await Task.Delay(Poll, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                p.Dispose();
                throw;
            }
        }
    }

    private static bool IsConfirmed(UpdateState s, PendingInstall pending) =>
        s.Pending is null || (s.Pending.ToVersion == pending.ToVersion && s.Pending.Confirmed);

    private static bool HasExited(IUpdateProcess p)
    {
        try
        {
            return p.HasExited;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task<HandoverOutcome> RollbackAsync(UpdatePaths paths, UpdateStateStore store,
        PendingInstall pending, Func<string, IUpdateProcess> start, IUpdateProcess? p, TimeProvider time,
        ILogger? log, CancellationToken ct)
    {
        if (p is not null)
        {
            try
            {
                if (!HasExited(p))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log?.LogInformation("Update rollback: the new process couldn't be killed ({Type}).",
                            ex.GetType().Name);
                    }
                    using var killWait = new CancellationTokenSource(KillWait, time);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(killWait.Token, ct);
                    try
                    {
                        await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        log?.LogInformation("Update rollback: the new process didn't exit within 5 s.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log?.LogInformation("Update rollback: waiting for the new process failed ({Type}).",
                            ex.GetType().Name);
                    }
                }
            }
            finally
            {
                p.Dispose();
            }
        }

        if (!File.Exists(paths.OldExePath))
        {
            log?.LogCritical("Update rollback failed: the old exe is missing; run install.ps1 to repair.");
            return HandoverOutcome.RollbackFailed;
        }

        try
        {
            await UpdateFiles.RetryAsync(() =>
            {
                if (File.Exists(paths.ExePath))
                {
                    File.Delete(paths.ExePath);
                }
                File.Move(paths.OldExePath, paths.ExePath);
            }, RestoreRetryFor, UpdateInstall.RetryEvery, time, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogCritical("Update rollback failed; run install.ps1 to repair.");
            return HandoverOutcome.RollbackFailed;
        }

        try
        {
            store.Update(s =>
            {
                s.FailedVersion = pending.ToVersion;
                s.Pending = null;
                s.Staged = null;
                s.Notice = new UpdateNotice(UpdateNoticeKind.RolledBack, pending.ToVersion, pending.FromVersion);
            });
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogWarning("Update rollback: the state file couldn't be written ({Type}).", ex.GetType().Name);
        }

        try
        {
            start(paths.ExePath).Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogError("Update rollback: the old version couldn't be started ({Type}).", ex.GetType().Name);
        }
        log?.LogWarning("Update to {To} rolled back to {From}", pending.ToVersion, pending.FromVersion);
        return HandoverOutcome.RolledBack;
    }
}

/// <param name="InstallNow">The staged update to install before anything else starts, or null.</param>
/// <param name="ConfirmAfterStart">This process is the new version of a pending handover.</param>
/// <param name="Notice">A notice to show once after the tray is up (already cleared from the state), or null.</param>
public sealed record StartupUpdatePlan(StagedUpdate? InstallNow, bool ConfirmAfterStart, UpdateNotice? Notice)
{
    public static StartupUpdatePlan Nothing { get; } = new(null, false, null);
}

public static class UpdateStartup
{
    public static readonly TimeSpan OldDeleteRetryFor = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan OldDeleteRetryEvery = TimeSpan.FromSeconds(1);

    /// <summary>SPEC 20.9 "UpdateStartup.Prepare". Call only for the installed copy. Never throws: I/O errors are
    /// logged and the step is skipped.</summary>
    public static StartupUpdatePlan Prepare(UpdatePaths paths, UpdateStateStore store, SemVersion running,
        UpdateMode mode, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(running);

        var s = store.Load();
        var notice = s.Notice;
        s.Notice = null;
        var confirm = false;
        var runningText = running.ToString();

        if (s.Pending is { } pending)
        {
            if (pending.ToVersion == runningText)
            {
                confirm = true;
            }
            else
            {
                log?.LogWarning("Update to {To} didn't take; staying on {Running}", pending.ToVersion, runningText);
                s.FailedVersion = pending.ToVersion;
                notice = new UpdateNotice(UpdateNoticeKind.RolledBack, pending.ToVersion, runningText);
                s.Pending = null;
            }
        }

        if (s.Pending is null)
        {
            try
            {
                if (File.Exists(paths.ExePath) && File.Exists(paths.OldExePath))
                {
                    File.Delete(paths.OldExePath);
                }
            }
            catch (Exception ex) when (UpdateFiles.IsIo(ex))
            {
                log?.LogInformation("The old exe couldn't be deleted ({Type}).", ex.GetType().Name);
            }
        }

        string? keptPath = null;
        if (s.Staged is { } staged)
        {
            if (SemVersion.TryParse(staged.Version, out var sv)
                && UpdatePolicy.IsUpdate(sv, running, s.FailedVersion)
                && StagedFileOk(paths.StagedExePath(sv), staged.Size, log))
            {
                keptPath = Path.GetFullPath(paths.StagedExePath(sv));
            }
            else
            {
                s.Staged = null;
            }
        }
        CleanStaging(paths, keptPath, log);

        try
        {
            store.Save(s);
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogWarning("The update state couldn't be saved ({Type}).", ex.GetType().Name);
        }

        var installNow = mode == UpdateMode.Install && !confirm ? s.Staged : null;
        log?.LogInformation("Update start-up: install {InstallNow}, confirm {Confirm}, notice {Kind}",
            installNow?.Version ?? "-", confirm, notice?.Kind.ToString() ?? "-");
        return new StartupUpdatePlan(installNow, confirm, notice);
    }

    private static bool StagedFileOk(string path, long size, ILogger? log)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == size;
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogInformation("A staged file couldn't be checked ({Type}).", ex.GetType().Name);
            return false;
        }
    }

    private static void CleanStaging(UpdatePaths paths, string? keptPath, ILogger? log)
    {
        try
        {
            if (!Directory.Exists(paths.StagingDir))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(paths.StagingDir))
            {
                var name = Path.GetFileName(file);
                var isPartial = name.EndsWith(UpdatePaths.PartialSuffix, StringComparison.OrdinalIgnoreCase);
                var isStaged = UpdatePaths.TryParseStagedFileName(name, out _);
                if (!isPartial && !isStaged)
                {
                    continue;
                }
                if (!isPartial && keptPath is not null
                    && string.Equals(Path.GetFullPath(file), keptPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (UpdateFiles.IsIo(ex))
                {
                    log?.LogInformation("A staging file couldn't be deleted ({Type}).", ex.GetType().Name);
                }
            }
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogInformation("The staging folder couldn't be cleaned ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>Pending for this version and not yet confirmed → Confirmed = true; returns
    /// Notice(Updated, Pending.FromVersion, Pending.ToVersion). Otherwise null. Never throws (I/O errors → logged, null).</summary>
    public static UpdateNotice? Confirm(UpdateStateStore store, SemVersion running, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(running);
        UpdateNotice? notice = null;
        try
        {
            store.Update(s =>
            {
                if (s.Pending is { Confirmed: false } p && p.ToVersion == running.ToString())
                {
                    s.Pending = p with { Confirmed = true };
                    notice = new UpdateNotice(UpdateNoticeKind.Updated, p.FromVersion, p.ToVersion);
                }
            });
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogWarning("The update couldn't be confirmed ({Type}).", ex.GetType().Name);
            return null;
        }
        return notice;
    }

    /// <summary>Deletes paths.OldExePath, retrying every second for up to 60 s, then clears a confirmed Pending for
    /// this version. Never throws except OperationCanceledException for ct.</summary>
    public static async Task CleanupOldAsync(UpdatePaths paths, UpdateStateStore store, SemVersion running,
        TimeProvider? time = null, ILogger? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(running);
        time ??= TimeProvider.System;

        var start = time.GetUtcNow();
        while (File.Exists(paths.OldExePath))
        {
            try
            {
                File.Delete(paths.OldExePath);
            }
            catch (Exception ex) when (UpdateFiles.IsIo(ex))
            {
                if (time.GetUtcNow() - start >= OldDeleteRetryFor)
                {
                    break;
                }
                await Task.Delay(OldDeleteRetryEvery, time, ct).ConfigureAwait(false);
            }
        }

        if (File.Exists(paths.OldExePath))
        {
            log?.LogInformation("ClippysRevenge.exe.old is still in use; it is deleted at the next start.");
            return;
        }
        try
        {
            store.Update(s =>
            {
                if (s.Pending is { Confirmed: true } p && p.ToVersion == running.ToString())
                {
                    s.Pending = null;
                }
            });
        }
        catch (Exception ex) when (UpdateFiles.IsIo(ex))
        {
            log?.LogWarning("The update state couldn't be written ({Type}).", ex.GetType().Name);
        }
    }
}
