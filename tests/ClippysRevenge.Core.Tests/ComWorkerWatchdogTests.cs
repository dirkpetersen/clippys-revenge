using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class ComWorkerWatchdogTests
{
    private sealed class FakeApp
    {
        public int AttachThread;
        public int ReleaseThread;
    }

    private sealed class FakeAttacher : IComAttacher<FakeApp>
    {
        public volatile bool Running = true;
        public volatile ManualResetEventSlim? AttachGate;
        public volatile ManualResetEventSlim? ProbeGate;
        public readonly ConcurrentBag<FakeApp> Apps = [];
        public readonly ConcurrentBag<int> AttachEntered = [];
        private int _attach, _release, _probe;
        public int ReleaseCalls => Volatile.Read(ref _release);
        public int ProbeCalls => Volatile.Read(ref _probe);

        public FakeApp? TryAttach()
        {
            AttachEntered.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref _attach);
            AttachGate?.Wait();
            if (!Running)
            {
                return null;
            }
            var app = new FakeApp { AttachThread = Environment.CurrentManagedThreadId };
            Apps.Add(app);
            return app;
        }

        public void Probe(FakeApp app)
        {
            Interlocked.Increment(ref _probe);
            ProbeGate?.Wait();
        }

        public void Release(FakeApp app)
        {
            app.ReleaseThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _release);
        }
    }

    private static readonly TimeSpan Ms300 = TimeSpan.FromMilliseconds(300);

    private sealed class Rig : IDisposable
    {
        public FakeAttacher Attacher { get; } = new();
        public ActivityHub Hub { get; } = new();
        public ComWorker<FakeApp> Worker { get; }
        public readonly ConcurrentBag<Thread> Configured = [];
        public readonly ConcurrentQueue<OutlookConnectionState> States = new();
        private readonly List<ManualResetEventSlim> _gates = [];

        public Rig(TimeSpan? interactive = null, TimeSpan? background = null, TimeSpan? recovery = null,
            TimeSpan? probeInterval = null, bool start = true)
        {
            Worker = new ComWorker<FakeApp>(Attacher, Hub, new ComWorkerOptions
            {
                InitialBackoff = TimeSpan.FromMilliseconds(10),
                MaxBackoff = TimeSpan.FromMilliseconds(40),
                ProbeInterval = probeInterval ?? TimeSpan.FromHours(1),
                InteractiveTimeout = interactive ?? Ms300,
                BackgroundTimeout = background ?? Ms300,
                ProbeTimeout = Ms300,
                RecoveryInterval = recovery ?? TimeSpan.FromMilliseconds(50),
                WatchdogPeriod = TimeSpan.FromMilliseconds(20),
                MaxAbandonedThreads = 2,
                ConfigureThread = t => Configured.Add(t),
            });
            Worker.StateChanged += st => States.Enqueue(st);
            if (start)
            {
                Worker.Start();
            }
        }

        public ManualResetEventSlim Gate()
        {
            var g = new ManualResetEventSlim();
            _gates.Add(g);
            return g;
        }

        public Task<int> Hang(ManualResetEventSlim gate, ManualResetEventSlim? started = null,
            WorkPriority p = WorkPriority.Interactive, int result = 0, Action<int>? onThread = null) =>
            Worker.RunAsync(_ =>
            {
                onThread?.Invoke(Environment.CurrentManagedThreadId);
                started?.Set();
                gate.Wait();
                return result;
            }, p, Ct);

        public Task<int> Noop() => Worker.RunAsync(_ => 0, WorkPriority.Interactive, Ct);

        public void Dispose()
        {
            foreach (var g in _gates)
            {
                g.Set();
            }
            Attacher.AttachGate?.Set();
            Attacher.ProbeGate?.Set();
            Worker.Dispose();
            Hub.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void WaitUntil(Func<bool> cond, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < end, "Timed out waiting for condition.");
            Thread.Sleep(5);
        }
    }

    private static async Task<Rig> Hung(Action<Rig>? configure = null)
    {
        var r = new Rig();
        configure?.Invoke(r);
        await r.Noop();
        return r;
    }

    [Fact]
    public async Task InteractiveHangFaultsAndSetsNotResponding()
    {
        using var r = await Hung();
        var gate = r.Gate();
        var ex = await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate).WaitAsync(TimeSpan.FromSeconds(2), Ct));
        Assert.Equal(OutlookNotRespondingException.DefaultMessage, ex.Message);
        Assert.IsAssignableFrom<OutlookUnavailableException>(ex);
        Assert.Equal(OutlookConnectionState.NotResponding, r.Worker.State);
        Assert.Equal(ActivityState.OutlookUnavailable, r.Hub.State);
        Assert.Equal("not responding", r.Hub.Progress.Label);
    }

    [Fact]
    public async Task SecondInteractiveFailsFast()
    {
        using var r = await Hung();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate));
        var ran = false;
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() =>
            r.Worker.RunAsync(_ => { ran = true; return 0; }, WorkPriority.Interactive, Ct));
        Assert.True(sw.ElapsedMilliseconds < 100);
        Assert.False(ran);
    }

    [Fact]
    public async Task BackgroundDrainsAfterRecovery()
    {
        using var r = await Hung();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate));
        var before = r.Worker.ConnectionId;
        var bg = r.Worker.RunAsync(_ => 9, WorkPriority.Background, Ct);
        gate.Set();
        Assert.Equal(9, await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        Assert.True(r.Worker.ConnectionId > before);
        Assert.NotEqual(ActivityState.OutlookUnavailable, r.Hub.State);
    }

    [Fact]
    public async Task LateResultIgnoredAndReleasedOnOwnThread()
    {
        using var r = await Hung();
        var gate = r.Gate();
        var t = r.Hang(gate, result: 42);
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => t);
        var hungApp = r.Attacher.Apps.First();
        var releases = r.Attacher.ReleaseCalls;
        gate.Set();
        WaitUntil(() => r.Attacher.ReleaseCalls == releases + 1);
        Assert.Equal(hungApp.AttachThread, hungApp.ReleaseThread);
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => t);
    }

    [Fact]
    public async Task ReplacementThread()
    {
        using var r = await Hung();
        var gate = r.Gate();
        var hungId = 0;
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate, onThread: id => hungId = id));
        var after = await r.Worker.RunAsync(_ => Environment.CurrentManagedThreadId, WorkPriority.Background, Ct)
            .WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(r.Configured.Count >= 2);
        Assert.NotEqual(hungId, after);
        Assert.Contains(r.Configured, t => t.Name == "OutlookWorker#2");
    }

    [Fact]
    public async Task BackgroundTimeout()
    {
        using var r = await Hung();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate, p: WorkPriority.Background));
        Assert.Equal(OutlookConnectionState.NotResponding, r.Worker.State);
    }

    [Fact]
    public async Task QueuedInteractiveTimesOutWithoutAbandoning()
    {
        using var r = new Rig(interactive: TimeSpan.FromMilliseconds(200), background: TimeSpan.FromSeconds(5));
        await r.Noop();
        using var started = new ManualResetEventSlim();
        var bg = r.Worker.RunAsync(_ => { started.Set(); Thread.Sleep(600); return 1; }, WorkPriority.Background, Ct);
        started.Wait(Ct);
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Worker.RunAsync(_ => 2, WorkPriority.Interactive, Ct));
        Assert.Equal(OutlookConnectionState.Available, r.Worker.State);
        Assert.Equal(1, await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Single(r.Configured);
    }

    [Fact]
    public void IdleProbeHang()
    {
        using var r = new Rig(probeInterval: TimeSpan.FromMilliseconds(50));
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        var pg = r.Gate();
        r.Attacher.ProbeGate = pg;
        WaitUntil(() => r.Worker.State == OutlookConnectionState.NotResponding);
        pg.Set();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
    }

    [Fact]
    public async Task AttachHangFailsFast()
    {
        using var r = new Rig(start: false);
        r.Attacher.AttachGate = r.Gate();
        r.Worker.Start();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.NotResponding);
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Noop());
        Assert.Equal(0, r.Attacher.ProbeCalls);
    }

    [Fact]
    public async Task AbandonedThreadCap()
    {
        using var r = new Rig(start: false);
        var gate = r.Gate();
        r.Attacher.AttachGate = gate;
        r.Worker.Start();
        await Task.Delay(1000, Ct);
        Assert.True(r.Attacher.AttachEntered.Distinct().Count() <= 2);
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Noop());
        var bg = r.Worker.RunAsync(_ => 5, WorkPriority.Background, Ct);
        gate.Set();
        Assert.Equal(5, await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
    }

    [Fact]
    public async Task PerCallTimeout()
    {
        using var r = new Rig(interactive: TimeSpan.FromSeconds(10));
        await r.Noop();
        var gate = r.Gate();
        var t = r.Worker.RunAsync(_ => { gate.Wait(); return 0; }, WorkPriority.Interactive, Ct, TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => t.WaitAsync(TimeSpan.FromSeconds(1), Ct));
    }

    [Fact]
    public void ArgumentChecks()
    {
        using var r = new Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = r.Worker.RunAsync(_ => 0, WorkPriority.Interactive, Ct, TimeSpan.Zero); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = r.Worker.RunAsync(_ => 0, WorkPriority.Interactive, Ct, TimeSpan.FromMilliseconds(-1)); });
        using var hub = new ActivityHub();
        void Bad(ComWorkerOptions o) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new ComWorker<FakeApp>(new FakeAttacher(), hub, o));
        Bad(new ComWorkerOptions { ProbeTimeout = TimeSpan.Zero });
        Bad(new ComWorkerOptions { RecoveryInterval = TimeSpan.FromMilliseconds(-1) });
        Bad(new ComWorkerOptions { WatchdogPeriod = TimeSpan.Zero });
        Bad(new ComWorkerOptions { MaxAbandonedThreads = 0 });
    }

    [Fact]
    public async Task DisposeWhileLaneHung()
    {
        using var r = new Rig(interactive: TimeSpan.FromMinutes(5), background: TimeSpan.FromMinutes(5));
        await r.Noop();
        var gate = r.Gate();
        using var started = new ManualResetEventSlim();
        _ = r.Hang(gate, started);
        started.Wait(Ct);
        var queued = r.Worker.RunAsync(_ => 1, WorkPriority.Background, Ct);
        var configured = r.Configured.Count;
        var sw = Stopwatch.StartNew();
        r.Worker.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        gate.Set();
        await Task.Delay(200, Ct);
        Assert.Equal(configured, r.Configured.Count);
    }

    [Fact]
    public async Task ActivityEvent()
    {
        using var r = await Hung();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate));
        var errs = r.Hub.GetEvents().Where(e => e.Kind == ActivityKind.Error).ToList();
        Assert.Single(errs);
        Assert.Equal("Outlook isn't responding: a call took longer than 0.3 s", errs[0].Summary);
    }

    [Fact]
    public async Task ConnectionIdCounts()
    {
        using var r = new Rig(start: false);
        Assert.Equal(0, r.Worker.ConnectionId);
        r.Worker.Start();
        await r.Noop();
        Assert.Equal(1, r.Worker.ConnectionId);
        await Assert.ThrowsAsync<OutlookUnavailableException>(() =>
            r.Worker.RunAsync<int>(_ => throw new COMException("x", ComErrors.RPC_E_DISCONNECTED), WorkPriority.Interactive, Ct));
        WaitUntil(() => r.Worker.ConnectionId == 2);
    }

    [Fact]
    public async Task NudgeInNotResponding()
    {
        using var r = new Rig(recovery: TimeSpan.FromSeconds(10));
        await r.Noop();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate));
        gate.Set();
        r.Worker.Nudge();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RecoveryFindsOutlookGone()
    {
        using var r = await Hung();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate));
        r.Attacher.Running = false;
        gate.Set();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        var ex = await Assert.ThrowsAsync<OutlookUnavailableException>(() => r.Noop());
        Assert.Equal(typeof(OutlookUnavailableException), ex.GetType());
    }

    [Fact]
    public async Task StateChangedOrder()
    {
        using var r = await Hung();
        var gate = r.Gate();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => r.Hang(gate));
        var bg = r.Worker.RunAsync(_ => 1, WorkPriority.Background, Ct);
        gate.Set();
        await bg.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        Assert.Equal(
            [OutlookConnectionState.Available, OutlookConnectionState.NotResponding, OutlookConnectionState.Available],
            r.States.ToArray());
    }
}
