namespace ClippysRevenge.Core;

/// <summary>Higher numeric value wins (SPEC 6.1 table, bottom to top).</summary>
public enum ActivityState
{
    Idle = 0,
    Serving = 1,
    Drafting = 2,
    Paused = 3,
    Purging = 4,
    Syncing = 5,
    Backfilling = 6,
    OutlookUnavailable = 7,
    AwaitingConfirmation = 8,
    Error = 9,
}

public enum ActivityKind { Sync, Backfill, Purge, Tool, Draft, Invite, Blocked, Ai, Error, Update }

public readonly record struct ActivityProgress(int Done, int Total, string Label)
{
    public static ActivityProgress None { get; } = new(0, 0, "");
}

public sealed record ActivityEvent(
    long Id,
    DateTimeOffset Time,
    ActivityKind Kind,
    string Summary,
    TimeSpan? Duration,
    string? Mailbox);

public sealed class ActivityHub : IDisposable
{
    /// <summary>The Begin label of the drafting worker's Drafting scope (SPEC 6.1).</summary>
    public const string DraftingLabel = "drafting replies";

    public const int EventCapacity = 500;
    public static readonly TimeSpan ChangedInterval = TimeSpan.FromMilliseconds(100);

    private sealed class ScopeRecord(long seq, ActivityState state, string label)
    {
        public long Seq { get; } = seq;
        public ActivityState State { get; } = state;
        public string Label { get; } = label;
        public int Done { get; set; }
        public int Total { get; set; }
    }

    private sealed class Scope(ActivityHub hub, ScopeRecord record) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            hub.End(record);
        }
    }

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly List<ScopeRecord> _scopes = [];
    private readonly ActivityEvent?[] _events = new ActivityEvent?[EventCapacity];
    private int _head;
    private int _count;
    private long _lastId;
    private long _nextSeq;

    private bool _hasFired;
    private long _lastFire;
    private bool _timerArmed;
    private bool _pending;
    private bool _disposed;

    public ActivityHub(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action? Changed;

    public ActivityState State
    {
        get
        {
            lock (_lock)
            {
                return ComputeState();
            }
        }
    }

    public ActivityProgress Progress
    {
        get
        {
            lock (_lock)
            {
                var state = ComputeState();
                ScopeRecord? win = null;
                foreach (var s in _scopes)
                {
                    if (s.State == state && (win is null || s.Seq > win.Seq))
                    {
                        win = s;
                    }
                }
                return win is null ? ActivityProgress.None : new ActivityProgress(win.Done, win.Total, win.Label);
            }
        }
    }

    /// <summary>True while at least one open scope has exactly this state (SPEC 7).</summary>
    /// <exception cref="ArgumentOutOfRangeException">state is Idle or undefined.</exception>
    public bool IsActive(ActivityState state)
    {
        if (state == ActivityState.Idle || !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        lock (_lock)
        {
            foreach (var s in _scopes)
            {
                if (s.State == state)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public IDisposable Begin(ActivityState state, string label)
    {
        if (state == ActivityState.Idle)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Idle is the absence of activity and cannot be begun.");
        }
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        ArgumentNullException.ThrowIfNull(label);

        ScopeRecord record;
        bool fire;
        lock (_lock)
        {
            record = new ScopeRecord(++_nextSeq, state, label);
            _scopes.Add(record);
            fire = MarkChanged();
        }
        if (fire)
        {
            Raise();
        }
        return new Scope(this, record);
    }

    public void Report(int done, int total)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(done);
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        bool fire = false;
        lock (_lock)
        {
            ScopeRecord? target = null;
            foreach (var s in _scopes)
            {
                if (s.State is ActivityState.Backfilling or ActivityState.Syncing or ActivityState.Purging
                    && (target is null || s.Seq > target.Seq))
                {
                    target = s;
                }
            }
            if (target is not null)
            {
                target.Done = done;
                target.Total = total;
                fire = MarkChanged();
            }
        }
        if (fire)
        {
            Raise();
        }
    }

    /// <summary>Updates the newest active scope in exactly this state; no-op (no Changed) when there is none.</summary>
    /// <exception cref="ArgumentOutOfRangeException">state is Idle or undefined; done or total negative.</exception>
    public void Report(ActivityState state, int done, int total)
    {
        if (state == ActivityState.Idle || !Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(done);
        ArgumentOutOfRangeException.ThrowIfNegative(total);

        bool fire = false;
        lock (_lock)
        {
            ScopeRecord? target = null;
            foreach (var s in _scopes)
            {
                if (s.State == state && (target is null || s.Seq > target.Seq))
                {
                    target = s;
                }
            }
            if (target is not null)
            {
                target.Done = done;
                target.Total = total;
                fire = MarkChanged();
            }
        }
        if (fire)
        {
            Raise();
        }
    }
    public void Event(ActivityKind kind, string summary, TimeSpan? duration = null, string? mailbox = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        bool fire;
        lock (_lock)
        {
            var ev = new ActivityEvent(++_lastId, _time.GetUtcNow(), kind, summary, duration, mailbox);
            if (_count < EventCapacity)
            {
                _events[(_head + _count) % EventCapacity] = ev;
                _count++;
            }
            else
            {
                _events[_head] = ev;
                _head = (_head + 1) % EventCapacity;
            }
            fire = MarkChanged();
        }
        if (fire)
        {
            Raise();
        }
    }

    public IReadOnlyList<ActivityEvent> GetEvents()
    {
        lock (_lock)
        {
            var result = new ActivityEvent[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _events[(_head + i) % EventCapacity]!;
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _pending = false;
            _timer.Dispose();
        }
    }

    private void End(ScopeRecord record)
    {
        bool fire;
        lock (_lock)
        {
            _scopes.Remove(record);
            fire = MarkChanged();
        }
        if (fire)
        {
            Raise();
        }
    }

    private ActivityState ComputeState()
    {
        var max = ActivityState.Idle;
        foreach (var s in _scopes)
        {
            if (s.State > max)
            {
                max = s.State;
            }
        }
        return max;
    }

    /// <summary>Called under the lock. Returns true when the caller must raise Changed now.</summary>
    private bool MarkChanged()
    {
        if (_disposed)
        {
            return false;
        }
        var now = _time.GetTimestamp();
        if (!_timerArmed && (!_hasFired || _time.GetElapsedTime(_lastFire, now) >= ChangedInterval))
        {
            _hasFired = true;
            _lastFire = now;
            return true;
        }
        _pending = true;
        if (!_timerArmed)
        {
            var due = ChangedInterval - _time.GetElapsedTime(_lastFire, now);
            if (due < TimeSpan.Zero)
            {
                due = TimeSpan.Zero;
            }
            _timerArmed = true;
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        return false;
    }

    private void OnTimer(object? state)
    {
        lock (_lock)
        {
            _timerArmed = false;
            if (_disposed || !_pending)
            {
                return;
            }
            _pending = false;
            _hasFired = true;
            _lastFire = _time.GetTimestamp();
        }
        Raise();
    }

    private void Raise()
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
            catch
            {
                // Subscriber failures must never crash a timer thread, the COM worker or a request.
            }
        }
    }
}
