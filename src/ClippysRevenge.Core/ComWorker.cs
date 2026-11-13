using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

public sealed class ComWorkerOptions
{
    public TimeSpan InitialBackoff { get; init; } = AttachBackoff.DefaultInitial;
    public TimeSpan MaxBackoff { get; init; } = AttachBackoff.DefaultMax;
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Called once on the new thread object before it starts (App: STA). Null = none.</summary>
    public Action<Thread>? ConfigureThread { get; init; }

    public string ThreadName { get; init; } = "OutlookWorker";
    public ILogger? Logger { get; init; }
}

/// <summary>
/// One thread owns the COM instance (SPEC 4). Interactive work goes first; background work only
/// runs while an instance is attached.
/// </summary>
public class ComWorker<TApp> : IDisposable where TApp : class
{
    public const int MaxBackgroundChunk = 50;

    private abstract class Item
    {
        public abstract bool IsDone { get; }
        public abstract Exception? Run(TApp app);
        public abstract void Fail(Exception ex);
    }

    private sealed class Item<T> : Item
    {
        private readonly Func<TApp, T> _work;
        public TaskCompletionSource<T> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Item(Func<TApp, T> work) => _work = work;

        public override bool IsDone => Tcs.Task.IsCompleted;

        public override Exception? Run(TApp app)
        {
            try
            {
                Tcs.TrySetResult(_work(app));
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        public override void Fail(Exception ex) => Tcs.TrySetException(ex);
    }

    private readonly IComAttacher<TApp> _attacher;
    private readonly ActivityHub _hub;
    private readonly ComWorkerOptions _options;
    private readonly ILogger? _log;
    private readonly AttachBackoff _backoff;
    private readonly Lock _lock = new();
    private readonly Queue<Item> _interactive = new();
    private readonly Queue<Item> _background = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly long _epoch = Stopwatch.GetTimestamp();

    private Thread? _thread;
    private volatile bool _disposed;
    private int _nudge;
    private int _cleanedUp;
    private volatile int _state = (int)OutlookConnectionState.Unknown;

    // Worker thread only.
    private TApp? _instance;
    private TimeSpan _nextAttempt;
    private TimeSpan _lastActivity;
    private IDisposable? _scope;

    public ComWorker(IComAttacher<TApp> attacher, ActivityHub hub, ComWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(attacher);
        ArgumentNullException.ThrowIfNull(hub);
        _attacher = attacher;
        _hub = hub;
        _options = options ?? new ComWorkerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.ProbeInterval, TimeSpan.Zero, nameof(options));
        _backoff = new AttachBackoff(_options.InitialBackoff, _options.MaxBackoff);
        _log = _options.Logger;
    }

    public OutlookConnectionState State => (OutlookConnectionState)_state;

    public event Action<OutlookConnectionState>? StateChanged;

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                throw new InvalidOperationException("The worker is already started.");
            }
            var thread = new Thread(Loop) { IsBackground = true, Name = _options.ThreadName };
            _options.ConfigureThread?.Invoke(thread);
            _thread = thread;
            thread.Start();
        }
    }

    public Task<T> RunAsync<T>(Func<TApp, T> work, WorkPriority priority, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var item = new Item<T>(work);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (priority == WorkPriority.Interactive ? _interactive : _background).Enqueue(item);
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
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            thread = _thread;
        }
        _wake.Release();
        if (thread is null)
        {
            Cleanup();
        }
        else if (Thread.CurrentThread != thread)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private TimeSpan Now => Stopwatch.GetElapsedTime(_epoch);

    private void Loop()
    {
        _nextAttempt = Now;
        _lastActivity = Now;
        while (!_disposed)
        {
            try
            {
                Step();
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "COM worker loop error.");
                _wake.Wait(100);
            }
        }
        Cleanup();
    }

    private void Step()
    {
        if (Interlocked.Exchange(ref _nudge, 0) == 1 && _instance is null)
        {
            _backoff.Reset();
            _nextAttempt = Now;
        }

        var item = Dequeue(_interactive);
        if (item is not null)
        {
            if (_instance is null)
            {
                AttachNow(scheduled: false);
            }
            if (_instance is null)
            {
                item.Fail(new OutlookUnavailableException());
            }
            else
            {
                RunItem(item);
            }
            return;
        }

        if (_instance is not null)
        {
            item = Dequeue(_background);
            if (item is not null)
            {
                RunItem(item);
                return;
            }
        }

        var now = Now;
        if (_instance is null)
        {
            if (now >= _nextAttempt)
            {
                AttachNow(scheduled: true);
                return;
            }
            _wake.Wait(Clamp(_nextAttempt - now));
            return;
        }

        var probeAt = _lastActivity + _options.ProbeInterval;
        if (now >= probeAt)
        {
            Probe();
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

    private void AttachNow(bool scheduled)
    {
        TApp? app = null;
        try
        {
            app = _attacher.TryAttach();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Attaching to Outlook failed.");
        }

        if (app is not null)
        {
            _instance = app;
            _backoff.Reset();
            _lastActivity = Now;
            SetState(OutlookConnectionState.Available);
            return;
        }
        SetState(OutlookConnectionState.Unavailable);
        if (scheduled)
        {
            _nextAttempt = Now + _backoff.Next();
        }
    }

    private void RunItem(Item item)
    {
        var failure = item.Run(_instance!);
        if (failure is not null)
        {
            if (ComErrors.IsOutlookGone(failure))
            {
                Drop();
                item.Fail(new OutlookUnavailableException(failure));
            }
            else
            {
                item.Fail(failure);
            }
        }
        _lastActivity = Now;
    }

    private void Probe()
    {
        try
        {
            _attacher.Probe(_instance!);
        }
        catch (Exception ex)
        {
            if (ComErrors.IsOutlookGone(ex))
            {
                Drop();
            }
            else
            {
                _log?.LogDebug(ex, "Outlook probe failed; keeping the instance.");
            }
        }
        _lastActivity = Now;
    }

    private void Drop()
    {
        ReleaseInstance();
        _backoff.Reset();
        _nextAttempt = Now + _backoff.Next();
        SetState(OutlookConnectionState.Unavailable);
    }

    private void ReleaseInstance()
    {
        var app = _instance;
        _instance = null;
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

    private void SetState(OutlookConnectionState next)
    {
        if (State == next)
        {
            return;
        }
        _state = (int)next;
        if (next == OutlookConnectionState.Unavailable)
        {
            _scope ??= _hub.Begin(ActivityState.OutlookUnavailable, "");
        }
        else
        {
            _scope?.Dispose();
            _scope = null;
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

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
        {
            return;
        }
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
        ReleaseInstance();
        _scope?.Dispose();
        _scope = null;
    }
}
