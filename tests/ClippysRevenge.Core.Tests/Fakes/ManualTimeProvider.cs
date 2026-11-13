namespace ClippysRevenge.Core.Tests;

/// <summary>Thread-safe fake clock. Timers fire inside <see cref="Advance"/>, outside the clock's lock.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public static readonly DateTimeOffset T0 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = T0;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override long GetTimestamp()
    {
        lock (_lock) return (_now - T0).Ticks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        t.Change(dueTime, period);
        lock (_lock) _timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_lock) target = _now + by;
        while (true)
        {
            ManualTimer? next = null;
            lock (_lock)
            {
                foreach (var t in _timers)
                    if (t.Due is { } d && d <= target && (next is null || d < next.Due))
                        next = t;
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due!.Value > _now) _now = next.Due.Value;
                next.Fired();
            }
            next.Invoke();
        }
    }

    private void Remove(ManualTimer t)
    {
        lock (_lock) _timers.Remove(t);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                _period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }
            return true;
        }

        // Called under the owner's lock.
        public void Fired() => Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;

        public void Invoke() => callback(state);

        public void Dispose()
        {
            Due = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
