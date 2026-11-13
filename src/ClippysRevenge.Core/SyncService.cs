using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>Background indexing for every enabled mailbox (SPEC §10). Thread-safe.</summary>
public sealed class SyncService : BackgroundService
{
    private readonly IMailSource _source;
    private readonly MailboxIndexes _indexes;
    private readonly Func<Settings> _settings;
    private readonly ActivityHub _hub;
    private readonly BalloonCenter _balloons;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly SyncEngine _engine;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _errorScopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notMountedReported = new(StringComparer.Ordinal);
    private readonly HashSet<(string, string)> _missingReported = [];
    private IReadOnlyList<string>? _planKeys;
    private CancellationTokenSource? _cycleCts;
    private IDisposable? _pausedScope;
    private bool _paused;
    private bool _running;
    private DateTimeOffset? _lastSync;
    private DateTimeOffset? _cycleEnd;
    private int _rebuilds;

    public SyncService(IMailSource source, MailboxIndexes indexes, Func<Settings> settings,
        ActivityHub hub, BalloonCenter balloons, TimeProvider? time = null, ILogger<SyncService>? logger = null,
        IReconcileSource? reconcile = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(balloons);
        _source = source;
        _indexes = indexes;
        _settings = settings;
        _hub = hub;
        _balloons = balloons;
        _time = time ?? TimeProvider.System;
        _log = logger;
        _engine = new SyncEngine(source, hub, _time, logger, reconcile);
    }

    /// <summary>Test hook; raised at the end of every cycle.</summary>
    internal event Action<SyncCycleResult>? CycleFinished;

    public bool IsPaused
    {
        get
        {
            lock (_lock) return _paused;
        }
    }

    /// <summary>Ignored while paused.</summary>
    public void SyncNow()
    {
        if (!IsPaused) Wake();
    }

    /// <summary>
    /// Applies a RetentionMonths change at once, outside the cycle (needs no COM, so it works while Outlook is
    /// unavailable or sync is paused), then wakes the loop unless paused.
    /// </summary>
    public void NotifySettingsChanged() => _ = Task.Run(ApplyRetentionAsync);

    private async Task ApplyRetentionAsync()
    {
        try
        {
            CancellationTokenSource? cts;
            lock (_lock) cts = _cycleCts;
            Cancel(cts);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var plan in SyncPlanner.Build(_settings().Clone()))
                {
                    try
                    {
                        _engine.ApplyRetention(plan, _indexes.Add(plan.Mailbox));
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning(ex, "Applying a RetentionMonths change failed.");
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Applying a RetentionMonths change failed.");
        }
        if (!IsPaused) Wake();
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    public void Pause()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_paused) return;
            _paused = true;
            _pausedScope = _hub.Begin(ActivityState.Paused, "");
            cts = _cycleCts;
        }
        Cancel(cts);
    }

    public void Resume()
    {
        IDisposable? scope;
        lock (_lock)
        {
            if (!_paused) return;
            _paused = false;
            scope = _pausedScope;
            _pausedScope = null;
        }
        scope?.Dispose();
        Wake();
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// "" = own, "all" = own + every open shared index, smtp = that shared index.
    /// ArgumentException: a shared mailbox that isn't open in MailboxIndexes.
    /// </summary>
    public async Task RebuildAsync(string mailbox, CancellationToken ct = default)
    {
        var key = MailboxIndexes.Normalize(mailbox);
        IReadOnlyList<string> targets;
        var open = _indexes.Mailboxes;
        if (key == MailboxIndexes.AllMailboxes) targets = open;
        else if (key.Length == 0) targets = [MailboxIndexes.OwnMailbox];
        else if (open.Contains(key, StringComparer.Ordinal)) targets = [key];
        else throw new ArgumentException(MailboxIndexes.Unknown(mailbox, open).Message, nameof(mailbox));

        Interlocked.Increment(ref _rebuilds);
        try
        {
            CancellationTokenSource? cts;
            lock (_lock) cts = _cycleCts;
            Cancel(cts);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _rebuilds);
            throw;
        }
        try
        {
            foreach (var k in targets)
            {
                IDisposable? scope;
                lock (_lock)
                {
                    _errorScopes.Remove(k, out scope);
                    _counts[k] = 0;
                }
                scope?.Dispose();
                _indexes.Reset(k);
                _hub.Event(ActivityKind.Backfill, "index deleted for rebuild", mailbox: k);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _rebuilds);
            _gate.Release();
            Wake();
        }
    }

    /// <summary>Thread-safe and never touches SQLite.</summary>
    public TrayStats GetStats()
    {
        lock (_lock)
        {
            long? count = null;
            if (_counts.Count > 0)
            {
                long sum = 0;
                var any = false;
                foreach (var (k, v) in _counts)
                {
                    if (_planKeys is not null && !_planKeys.Contains(k, StringComparer.Ordinal)) continue;
                    sum += v;
                    any = true;
                }
                if (any) count = sum;
            }
            DateTimeOffset? next = null;
            if (!_paused && !_running && _cycleEnd is { } end)
                next = end + TimeSpan.FromSeconds(_settings().SyncIntervalSeconds);
            return new TrayStats(count, _lastSync, next, _paused);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Run(FillCounts, CancellationToken.None).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!IsPaused) await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                await WaitAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            IDisposable[] scopes;
            IDisposable? paused;
            lock (_lock)
            {
                scopes = [.. _errorScopes.Values];
                _errorScopes.Clear();
                paused = _pausedScope;
                _pausedScope = null;
            }
            foreach (var s in scopes) s.Dispose();
            paused?.Dispose();
        }
    }

    private void FillCounts()
    {
        foreach (var k in _indexes.Mailboxes)
        {
            try
            {
                var total = _indexes.For(k).Status().TotalEmails;
                lock (_lock) _counts[k] = total;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Could not read the email count of a mailbox index.");
            }
        }
    }

    private async Task WaitAsync(CancellationToken stop)
    {
        if (IsPaused)
        {
            await _wake.Reader.WaitToReadAsync(stop).ConfigureAwait(false);
            _wake.Reader.TryRead(out _);
            return;
        }
        var interval = TimeSpan.FromSeconds(_settings().SyncIntervalSeconds);
        DateTimeOffset end;
        lock (_lock) end = _cycleEnd ?? _time.GetUtcNow();
        var delay = end + interval - _time.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var timer = Task.Delay(delay, _time, linked.Token);
            var signal = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();
            await Task.WhenAny(timer, signal).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            // Observe both losers.
            try { await Task.WhenAll(timer, signal).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            stop.ThrowIfCancellationRequested();
        }
        _wake.Reader.TryRead(out _);
    }

    private async Task RunCycleAsync(CancellationToken stop)
    {
        await _gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_lock)
            {
                if (_paused) return;
                _cycleCts = cts;
                _running = true;
            }
            if (Volatile.Read(ref _rebuilds) > 0) Cancel(cts);

            var ct = cts.Token;
            var results = new List<MailboxSyncResult>();
            var aborted = false;
            try
            {
                var plans = SyncPlanner.Build(_settings().Clone());
                lock (_lock) _planKeys = plans.Select(p => p.Mailbox).ToArray();
                foreach (var plan in plans)
                {
                    if (!await SyncOneAsync(plan, results, stop, ct).ConfigureAwait(false))
                    {
                        aborted = true;
                        break;
                    }
                }
            }
            finally
            {
                var now = _time.GetUtcNow();
                lock (_lock)
                {
                    _cycleCts = null;
                    _running = false;
                    _cycleEnd = now;
                    if (!aborted) _lastSync = now;
                }
            }
            CycleFinished?.Invoke(new SyncCycleResult(results, aborted));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns false when the cycle must stop.</summary>
    private async Task<bool> SyncOneAsync(MailboxPlan plan, List<MailboxSyncResult> results,
        CancellationToken stop, CancellationToken ct)
    {
        var key = plan.Mailbox;
        try
        {
            var index = _indexes.Add(key);
            var r = await _engine.SyncMailboxAsync(plan, index, ct).ConfigureAwait(false);
            results.Add(r);
            var total = index.Status().TotalEmails;
            lock (_lock) _counts[key] = total;
            OnSuccess(r, total);
            return true;
        }
        catch (MailSourceUnavailableException ex)
        {
            _log?.LogInformation(ex, "Outlook went away during sync; the cycle was stopped.");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            _log?.LogError(ex, "Sync of mailbox {Mailbox} failed.", key.Length == 0 ? "(own)" : key);
            _hub.Event(ActivityKind.Error, "sync failed: " + Truncate(ex.Message, 200), mailbox: key);
            lock (_lock)
            {
                if (!_errorScopes.ContainsKey(key))
                {
                    var prefix = SyncLabels.MailboxPrefix(key);
                    _errorScopes[key] = _hub.Begin(ActivityState.Error,
                        "sync failed for " + (key.Length == 0 ? "your mailbox" : prefix));
                }
            }
            return true;
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            return false;
        }
    }

    private void OnSuccess(MailboxSyncResult r, long total)
    {
        var key = r.Mailbox;
        if (r.NotMounted)
        {
            bool first;
            lock (_lock) first = _notMountedReported.Add(key);
            if (first)
                _hub.Event(ActivityKind.Error, $"{SyncLabels.MailboxName(key)} is not in Outlook; skipped", mailbox: key);
            return;
        }

        var events = new List<string>();
        IDisposable? errorScope;
        lock (_lock)
        {
            _notMountedReported.Remove(key);
            foreach (var f in r.Folders)
            {
                if (!f.Missing) _missingReported.Remove((key, f.Folder));
                else if (_missingReported.Add((key, f.Folder))) events.Add(f.Folder);
            }
            _errorScopes.Remove(key, out errorScope);
        }
        errorScope?.Dispose();
        foreach (var path in events)
            _hub.Event(ActivityKind.Error, $"folder \"{path}\" not found; skipped", mailbox: key);
        if (r.BackfillFinished)
            _balloons.Post(BalloonTexts.BackfillDone(SyncLabels.MailboxName(key), total));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
