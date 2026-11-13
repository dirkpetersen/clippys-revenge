using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public sealed class ComWorkerOptions
{
    public TimeSpan InitialBackoff { get; init; } = AttachBackoff.DefaultInitial;
    public TimeSpan MaxBackoff { get; init; } = AttachBackoff.DefaultMax;
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Called once on each new thread object before it starts (App: STA). Null = none.</summary>
    public Action<Thread>? ConfigureThread { get; init; }

    public string ThreadName { get; init; } = "OutlookWorker";
    public ILogger? Logger { get; init; }

    /// <summary>A COM call running longer than this is logged (name and duration only). SPEC 2.</summary>
    public TimeSpan SlowCallThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Interactive item: maximum time from RunAsync to completion (queue wait + run).</summary>
    public TimeSpan InteractiveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Background item: maximum run time once it has started (queue wait doesn't count).</summary>
    public TimeSpan BackgroundTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>One attach attempt (TryAttach, plus Probe when recovering) or one idle probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Delay between recovery attempts while NotResponding.</summary>
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the watchdog checks deadlines.</summary>
    public TimeSpan WatchdogPeriod { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Most abandoned (still blocked) threads alive at once; no new thread is started beyond it.</summary>
    public int MaxAbandonedThreads { get; init; } = 2;
}

/// <summary>
/// One live thread owns the COM instance (SPEC 4). Interactive work goes first; background work only
/// runs while an instance is attached. A watchdog abandons a thread whose call misses its deadline and
/// starts a replacement that re-attaches with TryAttach only.
/// </summary>
public class ComWorker<TApp> : IDisposable where TApp : class
{
    public const int MaxBackgroundChunk = 50;

    private abstract class Item
    {
        public bool Interactive { get; init; }
        public TimeSpan Timeout { get; init; }
        public TimeSpan EnqueuedAt { get; init; }
        public TimeSpan QueueDeadline => EnqueuedAt + Timeout;
        public abstract bool IsDone { get; }
        public abstract string Name { get; }
        public abstract Exception? Invoke(TApp app);
        public abstract void Commit();
        public abstract void Fail(Exception ex);
    }

    private sealed class Item<T> : Item
    {
        private readonly Func<TApp, T> _work;
        private T? _value;
        public TaskCompletionSource<T> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Item(Func<TApp, T> work) => _work = work;

        public override bool IsDone => Tcs.Task.IsCompleted;

        public override string Name => DescribeWork(_work);

        public override Exception? Invoke(TApp app)
        {
            try
            {
                _value = _work(app);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        public override void Commit() => Tcs.TrySetResult(_value!);

        public override void Fail(Exception ex) => Tcs.TrySetException(ex);
    }

    /// <summary>One STA thread with its own instance. Fields marked (lock) are guarded by the worker lock.</summary>
    private sealed class Lane
    {
        public required Thread Thread { get; init; }
        public bool Recovering { get; set; }
        public bool Immediate { get; init; }
        public volatile bool Abandoned;

        // Lane thread only.
        public TApp? Instance;
        public TimeSpan NextAttempt;
        public TimeSpan LastActivity;

        // (lock) The call in progress.
        public bool InCall;
        public TimeSpan Deadline;
        public TimeSpan CallTimeout;
        public Item? CallItem;
        public Item? WaitingItem;
        public string CallKind = "";
    }

    /// <summary>Method name of the delegate (never arguments). Lambdas show the enclosing method.</summary>
    internal static string DescribeWork(Delegate work)
    {
        var m = work.Method;
        var type = m.DeclaringType;
        while (type?.DeclaringType is not null && type.Name.StartsWith('<'))
        {
            type = type.DeclaringType;
        }
        var name = m.Name;
        if (name.StartsWith('<'))
        {
            var end = name.IndexOf('>');
            if (end > 1)
            {
                name = name[1..end];
            }
        }
        return type is null ? name : type.Name + "." + name;
    }

    private readonly IComAttacher<TApp> _attacher;
    private readonly ActivityHub _hub;
    private readonly ComWorkerOptions _options;
    private readonly ILogger? _log;
    private readonly AttachBackoff _backoff;
    private readonly Lock _lock = new();
    private readonly Lock _stateLock = new();
    private readonly Queue<Item> _interactive = new();
    private readonly Queue<Item> _background = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly long _epoch = Stopwatch.GetTimestamp();

    // (lock)
    private Lane? _live;
    private bool _started;
    private int _laneCount;
    private int _abandoned;
    private Timer? _watchdog;

    private volatile bool _disposed;
    private int _nudge;
    private long _connectionId;
    private volatile int _state = (int)OutlookConnectionState.Unknown;

    // (state lock)
    private IDisposable? _scope;

    public ComWorker(IComAttacher<TApp> attacher, ActivityHub hub, ComWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(attacher);
        ArgumentNullException.ThrowIfNull(hub);
        _attacher = attacher;
        _hub = hub;
        _options = options ?? new ComWorkerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.ProbeInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.InteractiveTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.BackgroundTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.ProbeTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.RecoveryInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.WatchdogPeriod, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxAbandonedThreads, 1, nameof(options));
        _backoff = new AttachBackoff(_options.InitialBackoff, _options.MaxBackoff);
        _log = _options.Logger;
    }

    public OutlookConnectionState State => (OutlookConnectionState)_state;

    /// <summary>Increments on every successful attach (any thread). 0 until the first one.</summary>
    public long ConnectionId => Interlocked.Read(ref _connectionId);

    /// <summary>Raised for every state change. May be raised on the watchdog thread or a worker thread.</summary>
    public event Action<OutlookConnectionState>? StateChanged;

    public void Start()
    {
        Lane lane;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The worker is already started.");
            }
            _started = true;
            lane = CreateLane(recovering: false, immediate: false);
            _watchdog = new Timer(_ => Tick(), null, _options.WatchdogPeriod, _options.WatchdogPeriod);
        }
        lane.Thread.Start();
    }

    /// <summary>Creates the live lane (not started). Call with the lock held; start the thread after releasing it.</summary>
    private Lane CreateLane(bool recovering, bool immediate)
    {
        var number = ++_laneCount;
        Lane? lane = null;
        var thread = new Thread(() => Loop(lane!))
        {
            IsBackground = true,
            Name = number == 1 ? _options.ThreadName : _options.ThreadName + "#" + number.ToString(CultureInfo.InvariantCulture),
        };
        _options.ConfigureThread?.Invoke(thread);
        lane = new Lane { Thread = thread, Recovering = recovering, Immediate = immediate };
        _live = lane;
        return lane;
    }

    /// <param name="timeout">
    /// Overrides InteractiveTimeout / BackgroundTimeout for this item (same meaning per priority).
    /// Null = the option for the priority. Must be greater than zero.
    /// </param>
    public Task<T> RunAsync<T>(Func<TApp, T> work, WorkPriority priority,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
        if (timeout is { } t)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(t, TimeSpan.Zero, nameof(timeout));
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var interactive = priority == WorkPriority.Interactive;
        if (interactive && State == OutlookConnectionState.NotResponding)
        {
            return Task.FromException<T>(new OutlookNotRespondingException());
        }

        var item = new Item<T>(work)
        {
            Interactive = interactive,
            Timeout = timeout ?? (interactive ? _options.InteractiveTimeout : _options.BackgroundTimeout),
            EnqueuedAt = Now,
        };
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (interactive ? _interactive : _background).Enqueue(item);
        }
        if (ct.CanBeCanceled)
        {
            var reg = ct.Register(static s => ((Item<T>)s!).Tcs.TrySetCanceled(), item);
            _ = item.Tcs.Task.ContinueWith(
                static (_, r) => ((CancellationTokenRegistration)r!).Dispose(),
                reg,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        _wake.Release();
        return item.Tcs.Task;
    }

    public Task<IReadOnlyList<TOut>> RunChunkedAsync<TIn, TOut>(
        IReadOnlyList<TIn> items,
        Func<TApp, IReadOnlyList<TIn>, IReadOnlyList<TOut>> work,
        CancellationToken ct = default,
        int chunkSize = MaxBackgroundChunk)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxBackgroundChunk);
        return RunChunkedCore(items, work, ct, chunkSize);
    }

    private async Task<IReadOnlyList<TOut>> RunChunkedCore<TIn, TOut>(
        IReadOnlyList<TIn> items,
        Func<TApp, IReadOnlyList<TIn>, IReadOnlyList<TOut>> work,
        CancellationToken ct,
        int chunkSize)
    {
        var result = new List<TOut>();
        foreach (var chunk in items.Chunk(chunkSize))
        {
            IReadOnlyList<TIn> c = chunk;
            var part = await RunAsync(app => work(app, c), WorkPriority.Background, ct).ConfigureAwait(false);
            if (part is null)
            {
                throw new InvalidOperationException("Chunk work returned null.");
            }
            result.AddRange(part);
        }
        return result;
    }

    public void Nudge()
    {
        if (_disposed || State == OutlookConnectionState.Available)
        {
            return;
        }
        Interlocked.Exchange(ref _nudge, 1);
        _wake.Release();
    }

    public void Dispose()
    {
        Thread? thread;
        Timer? watchdog;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            thread = _live?.Thread;
            watchdog = _watchdog;
            _watchdog = null;
        }
        watchdog?.Dispose();
        _wake.Release();
        if (thread is not null && thread.IsAlive && Thread.CurrentThread != thread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
        // Idempotent. Covers no live lane, an unstarted worker, or a live lane still stuck in a call.
        FailPending();
        DisposeScope();
    }

    private TimeSpan Now => Stopwatch.GetElapsedTime(_epoch);

    // ---- lane loop (live lane) ----

    private void Loop(Lane lane)
    {
        lane.NextAttempt = lane.Recovering && !lane.Immediate ? Now + _options.RecoveryInterval : Now;
        lane.LastActivity = Now;
        while (!_disposed && !lane.Abandoned)
        {
            try
            {
                Step(lane);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "COM worker loop error.");
                _wake.Wait(100);
            }
        }
        if (lane.Abandoned)
        {
            AbandonedExit(lane);
        }
        else
        {
            FailPending();
            ReleaseInstance(lane);
            DisposeScope();
        }
    }

    private void Step(Lane lane)
    {
        if (Interlocked.Exchange(ref _nudge, 0) == 1 && lane.Instance is null)
        {
            _backoff.Reset();
            lane.NextAttempt = Now;
        }

        var item = Dequeue(_interactive);
        if (item is not null)
        {
            if (lane.Recovering || Now >= item.QueueDeadline)
            {
                item.Fail(new OutlookNotRespondingException());
                return;
            }
            if (lane.Instance is null)
            {
                AttachNow(lane, scheduled: false, waiting: item);
                if (lane.Abandoned)
                {
                    return;
                }
            }
            if (lane.Instance is null)
            {
                item.Fail(new OutlookUnavailableException());
            }
            else
            {
                RunItem(lane, item);
            }
            return;
        }

        if (lane.Instance is not null)
        {
            item = Dequeue(_background);
            if (item is not null)
            {
                RunItem(lane, item);
                return;
            }
        }

        var now = Now;
        if (lane.Instance is null)
        {
            if (now >= lane.NextAttempt)
            {
                if (lane.Recovering)
                {
                    Recover(lane);
                }
                else
                {
                    AttachNow(lane, scheduled: true, waiting: null);
                }
                return;
            }
            _wake.Wait(Clamp(lane.NextAttempt - now));
            return;
        }

        var probeAt = lane.LastActivity + _options.ProbeInterval;
        if (now >= probeAt)
        {
            Probe(lane);
            return;
        }
        _wake.Wait(Clamp(probeAt - now));
    }

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private Item? Dequeue(Queue<Item> queue)
    {
        lock (_lock)
        {
            while (queue.TryDequeue(out var item))
            {
                if (!item.IsDone)
                {
                    return item;
                }
            }
            return null;
        }
    }

    private void BeginCall(Lane lane, string kind, TimeSpan deadline, TimeSpan timeout, Item? item, Item? waiting)
    {
        lock (_lock)
        {
            lane.InCall = true;
            lane.Deadline = deadline;
            lane.CallTimeout = timeout;
            lane.CallKind = kind;
            lane.CallItem = item;
            lane.WaitingItem = waiting;
        }
    }

    /// <summary>Clears the call slot. False when the watchdog abandoned this lane meanwhile.</summary>
    private bool EndCall(Lane lane)
    {
        lock (_lock)
        {
            lane.InCall = false;
            lane.CallItem = null;
            lane.WaitingItem = null;
            return !lane.Abandoned;
        }
    }

    private void AttachNow(Lane lane, bool scheduled, Item? waiting)
    {
        TApp? app = null;
        BeginCall(lane, "attach", Now + _options.ProbeTimeout, _options.ProbeTimeout, null, waiting);
        try
        {
            app = TimedCall("TryAttach", _attacher.TryAttach);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Attaching to Outlook failed.");
        }
        if (!EndCall(lane))
        {
            ReleaseApp(app);
            return;
        }

        if (app is not null)
        {
            lane.Instance = app;
            _backoff.Reset();
            lane.LastActivity = Now;
            Interlocked.Increment(ref _connectionId);
            SetState(OutlookConnectionState.Available);
            return;
        }
        SetState(OutlookConnectionState.Unavailable);
        if (scheduled)
        {
            lane.NextAttempt = Now + _backoff.Next();
        }
    }

    // One recovery attempt: TryAttach then Probe under a single deadline (never launches Outlook).
    private void Recover(Lane lane)
    {
        TApp? app = null;
        Exception? error = null;
        BeginCall(lane, "recovery", Now + _options.ProbeTimeout, _options.ProbeTimeout, null, null);
        try
        {
            app = TimedCall("TryAttach", _attacher.TryAttach);
            if (app is not null)
            {
                var probed = app;
                TimedCall("Probe", () => { _attacher.Probe(probed); return 0; });
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        if (!EndCall(lane))
        {
            ReleaseApp(app);
            return;
        }

        if (error is null && app is not null)
        {
            lane.Instance = app;
            lane.Recovering = false;
            _backoff.Reset();
            lane.LastActivity = Now;
            Interlocked.Increment(ref _connectionId);
            SetState(OutlookConnectionState.Available);
            return;
        }

        ReleaseApp(app);
        if (error is null || ComErrors.IsOutlookGone(error))
        {
            lane.Recovering = false;
            _backoff.Reset();
            lane.NextAttempt = Now + _backoff.Next();
            SetState(OutlookConnectionState.Unavailable);
            return;
        }
        _log?.LogDebug(error, "Outlook recovery attempt failed; trying again later.");
        lane.NextAttempt = Now + _options.RecoveryInterval;
    }

    private void RunItem(Lane lane, Item item)
    {
        var deadline = item.Interactive ? item.QueueDeadline : Now + item.Timeout;
        BeginCall(lane, item.Interactive ? "interactive" : "background", deadline, item.Timeout, item, null);
        var failure = TimedCall(item.Name, () => item.Invoke(lane.Instance!));
        if (!EndCall(lane))
        {
            return;
        }
        if (failure is not null)
        {
            if (ComErrors.IsOutlookGone(failure))
            {
                Drop(lane);
                item.Fail(new OutlookUnavailableException(failure));
            }
            else
            {
                item.Fail(failure);
            }
        }
        else
        {
            item.Commit();
        }
        lane.LastActivity = Now;
    }

    // Logs name and duration only. The timer line is what shows a call that never returns.
    private T TimedCall<T>(string name, Func<T> call)
    {
        if (_log is null)
        {
            return call();
        }
        var threshold = _options.SlowCallThreshold;
        var sw = Stopwatch.StartNew();
        using var timer = new Timer(
            _ => _log.LogWarning("COM call {Name} is still running after {Seconds:F0} s.", name, threshold.TotalSeconds),
            null, threshold, Timeout.InfiniteTimeSpan);
        try
        {
            return call();
        }
        finally
        {
            if (sw.Elapsed >= threshold)
            {
                _log.LogWarning("COM call {Name} took {Seconds:F1} s.", name, sw.Elapsed.TotalSeconds);
            }
        }
    }

    private void Probe(Lane lane)
    {
        Exception? error = null;
        BeginCall(lane, "probe", Now + _options.ProbeTimeout, _options.ProbeTimeout, null, null);
        try
        {
            TimedCall("Probe", () => { _attacher.Probe(lane.Instance!); return 0; });
        }
        catch (Exception ex)
        {
            error = ex;
        }
        if (!EndCall(lane))
        {
            return;
        }
        if (error is not null)
        {
            if (ComErrors.IsOutlookGone(error))
            {
                Drop(lane);
            }
            else
            {
                _log?.LogDebug(error, "Outlook probe failed; keeping the instance.");
            }
        }
        lane.LastActivity = Now;
    }

    private void Drop(Lane lane)
    {
        ReleaseInstance(lane);
        _backoff.Reset();
        lane.NextAttempt = Now + _backoff.Next();
        SetState(OutlookConnectionState.Unavailable);
    }

    private void ReleaseInstance(Lane lane)
    {
        var app = lane.Instance;
        lane.Instance = null;
        ReleaseApp(app);
    }

    private void ReleaseApp(TApp? app)
    {
        if (app is null)
        {
            return;
        }
        try
        {
            _attacher.Release(app);
        }
        catch (Exception ex)
        {
            _log?.LogDebug(ex, "Releasing the Outlook instance failed.");
        }
    }

    // ---- watchdog ----

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }
        List<Item>? expired = null;
        Lane? hung = null;
        Item? hungItem = null;
        Item? hungWaiting = null;
        var kind = "";
        TimeSpan timeout = default;
        var abandonedNow = 0;
        lock (_lock)
        {
            var now = Now;
            foreach (var item in _interactive)
            {
                if (!item.IsDone && now >= item.QueueDeadline)
                {
                    (expired ??= []).Add(item);
                }
            }
            if (_live is { InCall: true } lane && now >= lane.Deadline)
            {
                lane.Abandoned = true;
                lane.InCall = false;
                _live = null;
                abandonedNow = ++_abandoned;
                hung = lane;
                hungItem = lane.CallItem;
                hungWaiting = lane.WaitingItem;
                kind = lane.CallKind;
                timeout = lane.CallTimeout;
            }
        }

        if (expired is not null)
        {
            foreach (var item in expired)
            {
                item.Fail(new OutlookNotRespondingException());
            }
        }
        if (hung is null)
        {
            return;
        }

        hungItem?.Fail(new OutlookNotRespondingException());
        hungWaiting?.Fail(new OutlookNotRespondingException());
        SetState(OutlookConnectionState.NotResponding);

        var seconds = timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        var text = $"Outlook isn't responding: a call took longer than {seconds} s";
        try
        {
            _hub.Event(ActivityKind.Error, text);
        }
        catch (ObjectDisposedException)
        {
            // The hub is gone at shutdown.
        }
        _log?.LogWarning("{Text} (kind {Kind}, {Abandoned} abandoned thread(s)).", text, kind, abandonedNow);

        Lane? replacement = null;
        lock (_lock)
        {
            if (!_disposed && _live is null && _abandoned < _options.MaxAbandonedThreads)
            {
                replacement = CreateLane(recovering: true, immediate: false);
            }
        }
        replacement?.Thread.Start();
    }

    // The abandoned lane's call finally returned. Only release its own objects on its own thread.
    private void AbandonedExit(Lane lane)
    {
        ReleaseInstance(lane);
        Lane? replacement = null;
        var signal = false;
        lock (_lock)
        {
            _abandoned--;
            if (!_disposed)
            {
                if (_live is null)
                {
                    replacement = CreateLane(recovering: true, immediate: true);
                }
                else
                {
                    signal = true;
                }
            }
        }
        if (replacement is not null)
        {
            replacement.Thread.Start();
        }
        else if (signal)
        {
            Interlocked.Exchange(ref _nudge, 1);
            _wake.Release();
        }
    }

    // ---- state ----

    private void SetState(OutlookConnectionState next)
    {
        lock (_stateLock)
        {
            if (State == next)
            {
                return;
            }
            _state = (int)next;
            _scope?.Dispose();
            _scope = null;
            if (!_disposed)
            {
                if (next == OutlookConnectionState.Unavailable)
                {
                    _scope = _hub.Begin(ActivityState.OutlookUnavailable, "");
                }
                else if (next == OutlookConnectionState.NotResponding)
                {
                    _scope = _hub.Begin(ActivityState.OutlookUnavailable, OutlookConnection.NotRespondingLabel);
                }
            }
            _log?.LogInformation("Outlook connection: {State}", next);

            var handler = StateChanged;
            if (handler is null)
            {
                return;
            }
            foreach (var d in handler.GetInvocationList())
            {
                try
                {
                    ((Action<OutlookConnectionState>)d)(next);
                }
                catch
                {
                    // Subscriber failures must never stop the COM worker.
                }
            }
        }
    }

    private void DisposeScope()
    {
        lock (_stateLock)
        {
            _scope?.Dispose();
            _scope = null;
        }
    }

    private void FailPending()
    {
        List<Item> pending;
        lock (_lock)
        {
            pending = [.. _interactive, .. _background];
            _interactive.Clear();
            _background.Clear();
        }
        foreach (var item in pending)
        {
            item.Fail(new ObjectDisposedException(nameof(ComWorker<TApp>)));
        }
    }
}
