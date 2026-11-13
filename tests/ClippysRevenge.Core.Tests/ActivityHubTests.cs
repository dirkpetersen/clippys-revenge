using Xunit;

namespace ClippysRevenge.Core.Tests;

public class ActivityHubTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private sealed class FakeTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due { get; private set; }
            public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;
            public DateTimeOffset? DueAt { get; set; }
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime;
                Period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Fire()
            {
                if (Period == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                }
                else
                {
                    DueAt = owner._now + Period;
                }
                callback(state);
            }

            public void Dispose()
            {
                Disposed = true;
                DueAt = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly DateTimeOffset _start;
        private readonly List<FakeTimer> _timers = [];

        public ManualTimeProvider()
        {
            _start = _now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => (_now - _start).Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new FakeTimer(this, callback, state);
            t.Change(dueTime, period);
            _timers.Add(t);
            return t;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (true)
            {
                FakeTimer? next = null;
                foreach (var t in _timers)
                {
                    if (t.DueAt is { } d && d <= target && (next is null || d < next.DueAt))
                    {
                        next = t;
                    }
                }
                if (next is null)
                {
                    break;
                }
                if (next.DueAt!.Value > _now)
                {
                    _now = next.DueAt.Value;
                }
                next.Fire();
            }
            _now = target;
        }
    }

    private static readonly ActivityState[] NonIdle =
        Enum.GetValues<ActivityState>().Where(s => s != ActivityState.Idle).ToArray();

    public static TheoryData<ActivityState, ActivityState> Pairs()
    {
        var d = new TheoryData<ActivityState, ActivityState>();
        foreach (var low in NonIdle)
        {
            foreach (var high in NonIdle)
            {
                if (high > low)
                {
                    d.Add(low, high);
                }
            }
        }
        return d;
    }

    private static readonly TimeSpan Ms10 = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Ms100 = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Sec1 = TimeSpan.FromSeconds(1);

    // ---- State and scopes ----

    [Fact]
    public void NewHub_IsIdleWithNoProgressOrEvents()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        Assert.Equal(ActivityState.Idle, hub.State);
        Assert.Equal(ActivityProgress.None, hub.Progress);
        Assert.Empty(hub.GetEvents());
    }

    [Fact]
    public void Begin_SetsStateAndLabel_DisposeRestoresIdle()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var scope = hub.Begin(ActivityState.Syncing, "Inbox");
        Assert.Equal(ActivityState.Syncing, hub.State);
        Assert.Equal(new ActivityProgress(0, 0, "Inbox"), hub.Progress);
        scope.Dispose();
        Assert.Equal(ActivityState.Idle, hub.State);
        Assert.Equal(ActivityProgress.None, hub.Progress);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void HigherStateWins(ActivityState low, ActivityState high)
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var a = hub.Begin(low, "l");
        var b = hub.Begin(high, "h");
        Assert.Equal(high, hub.State);
        b.Dispose();
        Assert.Equal(low, hub.State);
        a.Dispose();

        var c = hub.Begin(high, "h");
        var d = hub.Begin(low, "l");
        Assert.Equal(high, hub.State);
        c.Dispose();
        Assert.Equal(low, hub.State);
        d.Dispose();
        Assert.Equal(ActivityState.Idle, hub.State);
    }

    [Fact]
    public void Precedence_MatchesSpecTable()
    {
        var sorted = Enum.GetValues<ActivityState>().OrderByDescending(s => s).ToArray();
        Assert.Equal(
            [
                ActivityState.Error, ActivityState.AwaitingConfirmation, ActivityState.OutlookUnavailable,
                ActivityState.Backfilling, ActivityState.Syncing, ActivityState.Purging,
                ActivityState.Paused, ActivityState.Drafting, ActivityState.Serving, ActivityState.Idle,
            ],
            sorted);
    }

    [Fact]
    public void ActivityKind_Order() =>
        Assert.Equal(
            [
                ActivityKind.Sync, ActivityKind.Backfill, ActivityKind.Purge, ActivityKind.Tool, ActivityKind.Draft,
                ActivityKind.Invite, ActivityKind.Blocked, ActivityKind.Ai, ActivityKind.Error,
                ActivityKind.Update,
            ],
            Enum.GetValues<ActivityKind>());

    [Fact]
    public void ReportState_UpdatesDraftingScope()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        using var s = hub.Begin(ActivityState.Drafting, ActivityHub.DraftingLabel);
        hub.Report(ActivityState.Drafting, 2, 5);
        Assert.Equal(ActivityState.Drafting, hub.State);
        Assert.Equal(new ActivityProgress(2, 5, "drafting replies"), hub.Progress);
    }

    [Fact]
    public void ReportState_IsolatedFromSyncReport()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        using var drafting = hub.Begin(ActivityState.Drafting, ActivityHub.DraftingLabel);
        var sync = hub.Begin(ActivityState.Syncing, "Inbox");
        hub.Report(ActivityState.Drafting, 1, 3);
        Assert.Equal(new ActivityProgress(0, 0, "Inbox"), hub.Progress);
        hub.Report(4, 10);
        Assert.Equal(new ActivityProgress(4, 10, "Inbox"), hub.Progress);
        sync.Dispose();
        Assert.Equal(new ActivityProgress(1, 3, "drafting replies"), hub.Progress);
    }

    [Fact]
    public void Report_DoesNotTouchDraftingScope()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        using var drafting = hub.Begin(ActivityState.Drafting, ActivityHub.DraftingLabel);
        hub.Report(7, 9);
        Assert.Equal(new ActivityProgress(0, 0, "drafting replies"), hub.Progress);
    }

    [Fact]
    public void ReportState_WithoutScope_IsNoOp()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        tp.Advance(Sec1);
        hub.Report(ActivityState.Drafting, 1, 1);
        Assert.Equal(0, fired);
        Assert.Equal(ActivityProgress.None, hub.Progress);
    }

    [Fact]
    public void ReportState_RejectsBadArguments()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Report(ActivityState.Idle, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Report((ActivityState)99, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Report(ActivityState.Drafting, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Report(ActivityState.Drafting, 0, -1));
    }

    [Fact]
    public void ReportState_UpdatesNewestMatchingScope()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var a = hub.Begin(ActivityState.Drafting, "a");
        var b = hub.Begin(ActivityState.Drafting, "b");
        hub.Report(ActivityState.Drafting, 3, 4);
        Assert.Equal(new ActivityProgress(3, 4, "b"), hub.Progress);
        b.Dispose();
        Assert.Equal(new ActivityProgress(0, 0, "a"), hub.Progress);
        a.Dispose();
    }

    [Fact]
    public void Event_AiKindIsRecorded()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        hub.Event(ActivityKind.Ai, "AI drafts: 1 drafted (3.0 s)");
        Assert.Equal(ActivityKind.Ai, hub.GetEvents()[0].Kind);
    }

    [Fact]
    public void SameState_IsRefCounted()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var a = hub.Begin(ActivityState.Syncing, "a");
        var b = hub.Begin(ActivityState.Syncing, "b");
        a.Dispose();
        Assert.Equal(ActivityState.Syncing, hub.State);
        b.Dispose();
        Assert.Equal(ActivityState.Idle, hub.State);
    }

    [Fact]
    public void ScopeDispose_IsIdempotent()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        var a = hub.Begin(ActivityState.Syncing, "a");
        var b = hub.Begin(ActivityState.Syncing, "b");
        tp.Advance(Sec1);
        a.Dispose();
        Assert.Equal(ActivityState.Syncing, hub.State);
        tp.Advance(Sec1);
        var before = fired;
        a.Dispose();
        tp.Advance(Sec1);
        Assert.Equal(ActivityState.Syncing, hub.State);
        Assert.Equal(before, fired);
        b.Dispose();
    }

    [Fact]
    public void Label_IsNewestScopeOfWinningState()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var a = hub.Begin(ActivityState.Syncing, "A");
        var b = hub.Begin(ActivityState.Syncing, "B");
        Assert.Equal("B", hub.Progress.Label);
        var serving = hub.Begin(ActivityState.Serving, "search_emails");
        Assert.Equal("B", hub.Progress.Label);
        serving.Dispose();
        b.Dispose();
        Assert.Equal("A", hub.Progress.Label);
        a.Dispose();
    }

    [Fact]
    public void Begin_RejectsIdleUndefinedAndNullLabel()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Begin(ActivityState.Idle, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Begin((ActivityState)99, "x"));
        Assert.Throws<ArgumentNullException>(() => hub.Begin(ActivityState.Syncing, null!));
        hub.Begin(ActivityState.Syncing, "").Dispose();
    }

    // ---- Progress ----

    [Fact]
    public void Report_UpdatesBackfillProgress()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        using var s = hub.Begin(ActivityState.Backfilling, "Inbox");
        hub.Report(10, 100);
        Assert.Equal(new ActivityProgress(10, 100, "Inbox"), hub.Progress);
    }

    [Fact]
    public void Report_IgnoresServingAndConfirmationScopes()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        using var backfill = hub.Begin(ActivityState.Backfilling, "Inbox");
        using var serving = hub.Begin(ActivityState.Serving, "search_emails");
        var confirm = hub.Begin(ActivityState.AwaitingConfirmation, "invite");
        hub.Report(20, 100);
        confirm.Dispose();
        Assert.Equal(ActivityState.Backfilling, hub.State);
        Assert.Equal(new ActivityProgress(20, 100, "Inbox"), hub.Progress);
    }

    [Fact]
    public void Report_WithoutSyncScope_IsNoOp()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        tp.Advance(Sec1);
        hub.Report(1, 2);
        Assert.Equal(0, fired);
        Assert.Equal(ActivityProgress.None, hub.Progress);

        using var serving = hub.Begin(ActivityState.Serving, "t");
        tp.Advance(Sec1);
        var before = fired;
        var progress = hub.Progress;
        hub.Report(1, 2);
        tp.Advance(Sec1);
        Assert.Equal(before, fired);
        Assert.Equal(progress, hub.Progress);
    }

    [Fact]
    public void Report_RejectsNegative()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        using var s = hub.Begin(ActivityState.Syncing, "x");
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Report(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Report(1, -5));
        hub.Report(7, 5);
        Assert.Equal(new ActivityProgress(7, 5, "x"), hub.Progress);
    }

    // ---- Events ----

    [Fact]
    public void Event_RecordsAllFields()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var t0 = tp.GetUtcNow();
        tp.Advance(TimeSpan.FromSeconds(5));
        hub.Event(ActivityKind.Tool, "search_emails · 23 results · 7 ms", TimeSpan.FromMilliseconds(7), "helpdesk@x.edu");
        hub.Event(ActivityKind.Sync, "Inbox +12");
        var events = hub.GetEvents();
        Assert.Equal(2, events.Count);
        var e = events[0];
        Assert.Equal(1, e.Id);
        Assert.Equal(t0 + TimeSpan.FromSeconds(5), e.Time);
        Assert.Equal(ActivityKind.Tool, e.Kind);
        Assert.Equal("search_emails · 23 results · 7 ms", e.Summary);
        Assert.Equal(TimeSpan.FromMilliseconds(7), e.Duration);
        Assert.Equal("helpdesk@x.edu", e.Mailbox);
        Assert.Equal(2, events[1].Id);
        Assert.Null(events[1].Duration);
        Assert.Null(events[1].Mailbox);
    }

    [Fact]
    public void Event_RingBufferKeepsLast500()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        for (var i = 0; i <= 500; i++)
        {
            hub.Event(ActivityKind.Sync, "e" + i);
        }
        var events = hub.GetEvents();
        Assert.Equal(500, events.Count);
        Assert.Equal("e1", events[0].Summary);
        Assert.Equal(2, events[0].Id);
        Assert.Equal("e500", events[499].Summary);
        Assert.Equal(501, events[499].Id);
    }

    [Fact]
    public void GetEvents_IsSnapshot()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        for (var i = 0; i < 3; i++)
        {
            hub.Event(ActivityKind.Sync, "x");
        }
        var s = hub.GetEvents();
        hub.Event(ActivityKind.Sync, "y");
        hub.Event(ActivityKind.Sync, "z");
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void Event_RejectsNullSummaryAndUndefinedKind()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        Assert.Throws<ArgumentNullException>(() => hub.Event(ActivityKind.Sync, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.Event((ActivityKind)99, "x"));
    }

    // ---- Throttle ----

    [Fact]
    public void Changed_FirstChangeFiresImmediately()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var fired = 0;
        hub.Changed += () => fired++;
        hub.Event(ActivityKind.Sync, "x");
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Changed_SecondChangeInWindow_FiresOnTrailingEdge()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        hub.Event(ActivityKind.Sync, "x");
        Assert.Equal(1, fired);
        tp.Advance(Ms10);
        hub.Event(ActivityKind.Sync, "x");
        Assert.Equal(1, fired);
        tp.Advance(TimeSpan.FromMilliseconds(89));
        Assert.Equal(1, fired);
        tp.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, fired);
        tp.Advance(Sec1);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Changed_BurstCoalesces()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        for (var i = 0; i < 50; i++)
        {
            hub.Event(ActivityKind.Sync, "x");
        }
        Assert.Equal(1, fired);
        tp.Advance(Ms100);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Changed_AfterQuietPeriod_FiresImmediately()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        hub.Event(ActivityKind.Sync, "x");
        tp.Advance(TimeSpan.FromMilliseconds(150));
        hub.Event(ActivityKind.Sync, "x");
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Changed_AtMost10PerSecond()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        for (var i = 0; i < 100; i++)
        {
            hub.Event(ActivityKind.Sync, "x");
            tp.Advance(Ms10);
        }
        tp.Advance(TimeSpan.FromMilliseconds(200));
        Assert.InRange(fired, 10, 11);
    }

    [Fact]
    public void Changed_DeliversFinalState()
    {
        var tp = new ManualTimeProvider();
        using var hub = new ActivityHub(tp);
        var last = ActivityState.Error;
        hub.Changed += () => last = hub.State;
        var a = hub.Begin(ActivityState.Syncing, "x");
        a.Dispose();
        tp.Advance(Ms100);
        Assert.Equal(ActivityState.Idle, last);
    }

    [Fact]
    public void Changed_SubscriberExceptionIsSwallowed()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var ran = 0;
        hub.Changed += () => throw new InvalidOperationException("boom");
        hub.Changed += () => ran++;
        hub.Begin(ActivityState.Syncing, "x").Dispose();
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task Changed_HandlerMayReenterHub()
    {
        using var hub = new ActivityHub(new ManualTimeProvider());
        var seen = false;
        hub.Changed += () =>
        {
            _ = hub.State;
            _ = hub.GetEvents();
            seen = true;
        };
        var task = Task.Run(() => hub.Event(ActivityKind.Sync, "x"), TestContext.Current.CancellationToken);
        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(seen);
    }

    [Fact]
    public void Dispose_StopsChanged()
    {
        var tp = new ManualTimeProvider();
        var hub = new ActivityHub(tp);
        var fired = 0;
        hub.Changed += () => fired++;
        hub.Event(ActivityKind.Sync, "x");
        Assert.Equal(1, fired);
        hub.Event(ActivityKind.Sync, "x");
        hub.Dispose();
        tp.Advance(Sec1);
        Assert.Equal(1, fired);
        hub.Event(ActivityKind.Sync, "y");
        hub.Begin(ActivityState.Syncing, "z").Dispose();
        hub.Report(1, 2);
        hub.Dispose();
        tp.Advance(Sec1);
        Assert.Equal(1, fired);
    }

    // ---- Concurrency ----

    [Fact]
    public void ConcurrentBeginDispose_EndsIdle()
    {
        using var hub = new ActivityHub();
        Parallel.For(0, 8, i =>
        {
            for (var n = 0; n < 1000; n++)
            {
                using (hub.Begin((ActivityState)(1 + i % 8), "t"))
                {
                }
            }
        });
        Assert.Equal(ActivityState.Idle, hub.State);
        Assert.Equal(ActivityProgress.None, hub.Progress);
    }

    [Fact]
    public void ConcurrentEvents_KeepUniqueIncreasingIds()
    {
        using var hub = new ActivityHub();
        Parallel.For(0, 8, _ =>
        {
            for (var n = 0; n < 1000; n++)
            {
                hub.Event(ActivityKind.Tool, "e");
            }
        });
        var events = hub.GetEvents();
        Assert.Equal(500, events.Count);
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i].Id > events[i - 1].Id);
        }
        Assert.Equal(8000, events[^1].Id);
    }

    [Fact]
    public void IsActive_TracksOpenScopes()
    {
        using var hub = new ActivityHub();
        Assert.False(hub.IsActive(ActivityState.Serving));
        var serving = hub.Begin(ActivityState.Serving, "a");
        var serving2 = hub.Begin(ActivityState.Serving, "b");
        var error = hub.Begin(ActivityState.Error, "e");
        Assert.Equal(ActivityState.Error, hub.State);
        Assert.True(hub.IsActive(ActivityState.Serving));
        Assert.True(hub.IsActive(ActivityState.Error));
        Assert.False(hub.IsActive(ActivityState.Drafting));
        error.Dispose();
        serving.Dispose();
        Assert.True(hub.IsActive(ActivityState.Serving));
        serving2.Dispose();
        Assert.False(hub.IsActive(ActivityState.Serving));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.IsActive(ActivityState.Idle));
        Assert.Throws<ArgumentOutOfRangeException>(() => hub.IsActive((ActivityState)99));
    }

    [Fact]
    public void ActivityKind_Update_IsLast()
    {
        Assert.Equal(ActivityKind.Update, Enum.GetValues<ActivityKind>()[^1]);
        Assert.Equal("Update", ActivityView.KindName(ActivityKind.Update));
    }
}
