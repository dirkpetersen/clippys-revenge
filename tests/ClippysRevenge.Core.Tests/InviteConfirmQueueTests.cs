using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class InviteConfirmQueueTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private sealed class FakeTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => DueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private readonly Lock _gate = new();
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<FakeTimer> _timers = [];

        public DateTimeOffset T0 { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var t = new FakeTimer(this, callback, state);
                t.Change(dueTime, period);
                _timers.Add(t);
                return t;
            }
        }

        public void SetNowWithoutFiring(DateTimeOffset now)
        {
            lock (_gate)
            {
                _now = now;
            }
        }

        public void Advance(TimeSpan by)
        {
            var target = GetUtcNow() + by;
            while (true)
            {
                FakeTimer? next = null;
                lock (_gate)
                {
                    foreach (var t in _timers)
                    {
                        if (t.DueAt is { } d && d <= target && (next is null || d < next.DueAt))
                        {
                            next = t;
                        }
                    }
                    if (next is not null && next.DueAt!.Value > _now)
                    {
                        _now = next.DueAt.Value;
                    }
                }
                if (next is null)
                {
                    break;
                }
                next.Fire();
            }
            SetNowWithoutFiring(target);
        }
    }

    private sealed class Call(InviteConfirmView view, DateTimeOffset deadline, CancellationToken dismiss)
    {
        public InviteConfirmView View { get; } = view;
        public DateTimeOffset Deadline { get; } = deadline;
        public CancellationToken Dismiss { get; } = dismiss;
        public TaskCompletionSource<InviteChoice?> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakePresenter : IInviteConfirmPresenter
    {
        private readonly Lock _gate = new();
        private int _outstanding;
        public List<Call> Calls { get; } = [];
        public bool AutoCompleteOnDismiss { get; set; } = true;
        public bool CompleteWithSendAutomatically { get; set; }
        public Exception? ThrowSync { get; set; }
        public Exception? FaultAsync { get; set; }
        public int MaxOutstanding { get; private set; }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return Calls.Count;
                }
            }
        }

        public Call this[int i]
        {
            get
            {
                lock (_gate)
                {
                    return Calls[i];
                }
            }
        }

        public Task<InviteChoice?> ShowAsync(InviteConfirmView view, DateTimeOffset deadline, CancellationToken dismiss)
        {
            if (ThrowSync is { } ts)
            {
                throw ts;
            }
            if (FaultAsync is { } fa)
            {
                return Task.FromException<InviteChoice?>(fa);
            }
            var call = new Call(view, deadline, dismiss);
            lock (_gate)
            {
                Calls.Add(call);
                _outstanding++;
                MaxOutstanding = Math.Max(MaxOutstanding, _outstanding);
            }
            if (AutoCompleteOnDismiss)
            {
                dismiss.Register(() => call.Result.TrySetResult(null));
            }
            var task = call.Result.Task.ContinueWith(t =>
            {
                lock (_gate)
                {
                    _outstanding--;
                }
                return t.Result;
            }, TaskScheduler.Default);
            if (CompleteWithSendAutomatically)
            {
                call.Result.TrySetResult(InviteChoice.Send);
            }
            return task;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ManualTimeProvider Time { get; } = new();
        public FakePresenter Presenter { get; } = new();
        public ActivityHub Hub { get; }
        public InviteConfirmQueue Queue { get; }

        public Fixture()
        {
            Hub = new ActivityHub(Time);
            Queue = new InviteConfirmQueue(Presenter, Hub, Time);
        }

        public void Dispose()
        {
            Queue.Dispose();
            Hub.Dispose();
        }
    }

    private static InviteConfirmView View(string subject = "S") =>
        InviteConfirmView.Create(new InviteConfirmRequest("Me <me@x.edu>", false, subject,
            new DateTime(2024, 10, 15, 13, 0, 0), new DateTime(2024, 10, 15, 14, 0, 0), null, false,
            [new InviteAttendee("", "a@x.edu", false)], "b"), "PT", 8, 17);

    private static readonly TimeSpan Two = TimeSpan.FromSeconds(120);

    private static async Task WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(cond(), "condition not reached");
    }

    [Fact]
    public async Task Q1_SendClick()
    {
        using var f = new Fixture();
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        var o = await task;
        Assert.Equal(new InviteConfirmOutcome(InviteChoice.Send, false), o);
        Assert.Equal(1, f.Presenter.Count);
        Assert.Equal(f.Time.T0 + Two, f.Presenter[0].Deadline);
        Assert.Equal(InviteDecision.Send, InviteGate.Decide(MeetingInviteMode.Send, o.Choice, o.TimedOut));
    }

    [Theory]
    [InlineData(InviteChoice.SaveDraft)]
    [InlineData(InviteChoice.Cancel)]
    [InlineData(null)]
    public async Task Q2_OtherAnswers(InviteChoice? choice)
    {
        using var f = new Fixture();
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        f.Presenter[0].Result.SetResult(choice);
        var o = await task;
        Assert.Equal(new InviteConfirmOutcome(choice, false), o);
        var expected = choice == InviteChoice.SaveDraft
            ? InviteDecision.SaveDraft(InviteDraftReason.UserChoice)
            : InviteDecision.Cancel;
        Assert.Equal(expected, InviteGate.Decide(MeetingInviteMode.Send, o.Choice, o.TimedOut));
    }

    [Fact]
    public async Task Q3_Timeout()
    {
        using var f = new Fixture();
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.Advance(Two);
        Assert.True(f.Presenter[0].Dismiss.IsCancellationRequested);
        var o = await task;
        Assert.Equal(new InviteConfirmOutcome(null, true), o);
        Assert.Equal(InviteDecision.SaveDraft(InviteDraftReason.Timeout),
            InviteGate.Decide(MeetingInviteMode.Send, o.Choice, o.TimedOut));
    }

    [Fact]
    public async Task Q4_ClickAfterDismissIgnored()
    {
        using var f = new Fixture();
        f.Presenter.AutoCompleteOnDismiss = false;
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.Advance(Two);
        Assert.False(task.IsCompleted);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        Assert.Equal(new InviteConfirmOutcome(null, true), await task);
    }

    [Theory]
    [InlineData(InviteChoice.Send, InviteChoice.Send)]
    [InlineData(InviteChoice.Cancel, InviteChoice.Cancel)]
    public async Task Q5_ClickAtDeadlineCountsAsTimeout(InviteChoice click, InviteChoice expected)
    {
        using var f = new Fixture();
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.SetNowWithoutFiring(f.Time.T0 + Two);
        f.Presenter[0].Result.SetResult(click);
        var o = await task;
        Assert.Equal(new InviteConfirmOutcome(expected, true), o);
        var d = InviteGate.Decide(MeetingInviteMode.Send, o.Choice, o.TimedOut);
        Assert.Equal(click == InviteChoice.Send ? InviteDecision.SaveDraft(InviteDraftReason.Timeout) : InviteDecision.Cancel, d);
    }

    [Fact]
    public async Task Q6_ClickJustBeforeDeadline()
    {
        using var f = new Fixture();
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.SetNowWithoutFiring(f.Time.T0 + TimeSpan.FromSeconds(119.999));
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        Assert.Equal(new InviteConfirmOutcome(InviteChoice.Send, false), await task);
    }

    [Fact]
    public async Task Q7_FifoOneAtATime()
    {
        using var f = new Fixture();
        f.Presenter.AutoCompleteOnDismiss = false;
        var a = View("A");
        var b = View("B");
        var c = View("C");
        var ta = f.Queue.ConfirmAsync(a, Two, CancellationToken.None);
        var tb = f.Queue.ConfirmAsync(b, Two, CancellationToken.None);
        var tc = f.Queue.ConfirmAsync(c, Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        Assert.Equal(3, f.Queue.Pending);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        await ta;
        await WaitFor(() => f.Presenter.Count == 2);
        Assert.Equal(2, f.Queue.Pending);
        f.Presenter[1].Result.SetResult(InviteChoice.Send);
        await tb;
        await WaitFor(() => f.Presenter.Count == 3);
        Assert.Equal(1, f.Queue.Pending);
        f.Presenter[2].Result.SetResult(InviteChoice.Send);
        await tc;
        Assert.Equal(0, f.Queue.Pending);
        Assert.Same(a, f.Presenter[0].View);
        Assert.Same(b, f.Presenter[1].View);
        Assert.Same(c, f.Presenter[2].View);
        Assert.Equal(1, f.Presenter.MaxOutstanding);
    }

    [Fact]
    public async Task Q8_TimeoutStartsWhenShown()
    {
        using var f = new Fixture();
        var ta = f.Queue.ConfirmAsync(View("A"), Two, CancellationToken.None);
        var tb = f.Queue.ConfirmAsync(View("B"), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.Advance(TimeSpan.FromSeconds(100));
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        await ta;
        await WaitFor(() => f.Presenter.Count == 2);
        Assert.Equal(f.Time.T0 + TimeSpan.FromSeconds(220), f.Presenter[1].Deadline);
        f.Time.Advance(TimeSpan.FromSeconds(100));
        Assert.False(f.Presenter[1].Dismiss.IsCancellationRequested);
        Assert.False(tb.IsCompleted);
        f.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(new InviteConfirmOutcome(null, true), await tb);
    }

    [Fact]
    public async Task Q9_CancelWhileQueued()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        var ta = f.Queue.ConfirmAsync(View("A"), Two, CancellationToken.None);
        var tb = f.Queue.ConfirmAsync(View("B"), Two, cts.Token);
        await WaitFor(() => f.Presenter.Count == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tb);
        Assert.Equal(1, f.Queue.Pending);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        await ta;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, f.Presenter.Count);
        Assert.Equal(0, f.Queue.Pending);
    }

    [Fact]
    public async Task Q10_CancelWhileShown()
    {
        using var f = new Fixture();
        f.Presenter.AutoCompleteOnDismiss = false;
        using var cts = new CancellationTokenSource();
        var task = f.Queue.ConfirmAsync(View(), Two, cts.Token);
        await WaitFor(() => f.Presenter.Count == 1);
        cts.Cancel();
        Assert.True(f.Presenter[0].Dismiss.IsCancellationRequested);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
        // Q17: no timer leftovers.
        f.Time.Advance(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Q11_AlreadyCancelled()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var task = f.Queue.ConfirmAsync(View(), Two, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, f.Presenter.Count);
        Assert.Equal(0, f.Queue.Pending);
    }

    [Fact]
    public async Task Q12_NextWaitsForWindowToClose()
    {
        using var f = new Fixture();
        f.Presenter.AutoCompleteOnDismiss = false;
        var ta = f.Queue.ConfirmAsync(View("A"), Two, CancellationToken.None);
        var tb = f.Queue.ConfirmAsync(View("B"), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Time.Advance(Two);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, f.Presenter.Count);
        f.Presenter[0].Result.SetResult(null);
        Assert.Equal(new InviteConfirmOutcome(null, true), await ta);
        await WaitFor(() => f.Presenter.Count == 2);
        f.Presenter[1].Result.SetResult(InviteChoice.Cancel);
        await tb;
    }

    [Fact]
    public async Task Q13_HubState()
    {
        using var f = new Fixture();
        Assert.Equal(ActivityState.Idle, f.Hub.State);
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        Assert.Equal(ActivityState.AwaitingConfirmation, f.Hub.State);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        await task;
        Assert.Equal(ActivityState.Idle, f.Hub.State);

        using var err = f.Hub.Begin(ActivityState.Error, "x");
        var t2 = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 2);
        Assert.Equal(ActivityState.Error, f.Hub.State);
        f.Presenter[1].Result.SetResult(InviteChoice.Send);
        await t2;
        Assert.Equal(ActivityState.Error, f.Hub.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Q14_PresenterFails(bool async)
    {
        using var f = new Fixture();
        var boom = new InvalidOperationException("boom");
        if (async)
        {
            f.Presenter.FaultAsync = boom;
        }
        else
        {
            f.Presenter.ThrowSync = boom;
        }
        var ex = await Assert.ThrowsAsync<McpException>(() => f.Queue.ConfirmAsync(View(), Two, CancellationToken.None));
        Assert.Equal(InviteConfirmTexts.DialogFailedMessage, ex.Message);
        Assert.Same(boom, ex.InnerException);
        Assert.Equal(ActivityState.Idle, f.Hub.State);

        f.Presenter.ThrowSync = null;
        f.Presenter.FaultAsync = null;
        var next = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        Assert.Equal(InviteChoice.Send, (await next).Choice);
    }

    [Fact]
    public async Task Q15_Dispose()
    {
        using var f = new Fixture();
        f.Presenter.AutoCompleteOnDismiss = false;
        var ta = f.Queue.ConfirmAsync(View("A"), Two, CancellationToken.None);
        var tb = f.Queue.ConfirmAsync(View("B"), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Queue.Dispose();
        var ea = await Assert.ThrowsAsync<McpException>(() => ta);
        var eb = await Assert.ThrowsAsync<McpException>(() => tb);
        Assert.Equal(InviteConfirmTexts.ShutdownMessage, ea.Message);
        Assert.Equal(InviteConfirmTexts.ShutdownMessage, eb.Message);
        Assert.True(f.Presenter[0].Dismiss.IsCancellationRequested);
        f.Presenter[0].Result.SetResult(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, f.Presenter.Count);
        var ec = await Assert.ThrowsAsync<McpException>(() => f.Queue.ConfirmAsync(View(), Two, CancellationToken.None));
        Assert.Equal(InviteConfirmTexts.ShutdownMessage, ec.Message);
        f.Queue.Dispose();
        Assert.Equal(ActivityState.Idle, f.Hub.State);
    }

    [Fact]
    public void Q16_ArgumentChecks()
    {
        using var f = new Fixture();
        Assert.Throws<ArgumentNullException>(() => { _ = f.Queue.ConfirmAsync(null!, Two, CancellationToken.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = f.Queue.ConfirmAsync(View(), TimeSpan.Zero, CancellationToken.None); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = f.Queue.ConfirmAsync(View(), TimeSpan.FromSeconds(-1), CancellationToken.None); });
    }

    [Fact]
    public async Task Q17_TimerCleanupAfterClick()
    {
        using var f = new Fixture();
        var task = f.Queue.ConfirmAsync(View(), Two, CancellationToken.None);
        await WaitFor(() => f.Presenter.Count == 1);
        f.Presenter[0].Result.SetResult(InviteChoice.Send);
        await task;
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.False(f.Presenter[0].Dismiss.IsCancellationRequested);
    }

    [Fact]
    public async Task Q18_Stress()
    {
        using var f = new Fixture();
        f.Presenter.CompleteWithSendAutomatically = true;
        var tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => f.Queue.ConfirmAsync(View("S" + i), Two, CancellationToken.None)))
            .ToArray();
        var outcomes = await Task.WhenAll(tasks);
        Assert.All(outcomes, o => Assert.Equal(new InviteConfirmOutcome(InviteChoice.Send, false), o));
        Assert.Equal(20, f.Presenter.Count);
        Assert.Equal(20, f.Presenter.Calls.Select(c => c.View).Distinct().Count());
        Assert.Equal(1, f.Presenter.MaxOutstanding);
    }
}
