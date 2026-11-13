using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class ComWorkerTests
{
    private sealed class FakeApp;

    private sealed class FakeAttacher : IComAttacher<FakeApp>
    {
        public volatile bool Running;
        public volatile Exception? AttachThrows;
        public volatile Exception? ProbeThrows;
        public readonly ConcurrentBag<int> Threads = [];
        private int _attach, _release, _probe;
        public int AttachCalls => Volatile.Read(ref _attach);
        public int ReleaseCalls => Volatile.Read(ref _release);
        public int ProbeCalls => Volatile.Read(ref _probe);

        public FakeApp? TryAttach()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref _attach);
            if (AttachThrows is { } ex)
            {
                throw ex;
            }
            return Running ? new FakeApp() : null;
        }

        public void Probe(FakeApp app)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref _probe);
            if (ProbeThrows is { } ex)
            {
                throw ex;
            }
        }

        public void Release(FakeApp app)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref _release);
        }
    }

    private sealed class Rig : IDisposable
    {
        public FakeAttacher Attacher { get; } = new();
        public ActivityHub Hub { get; } = new();
        public ComWorker<FakeApp> Worker { get; }

        public Rig(bool running = true, TimeSpan? initial = null, TimeSpan? probe = null, Action<Thread>? configure = null, bool start = true)
        {
            Attacher.Running = running;
            Worker = new ComWorker<FakeApp>(Attacher, Hub, new ComWorkerOptions
            {
                InitialBackoff = initial ?? TimeSpan.FromMilliseconds(10),
                MaxBackoff = initial is { } i && i > TimeSpan.FromMilliseconds(40) ? i : TimeSpan.FromMilliseconds(40),
                ProbeInterval = probe ?? TimeSpan.FromHours(1),
                ConfigureThread = configure,
            });
            if (start)
            {
                Worker.Start();
            }
        }

        public void Dispose()
        {
            Worker.Dispose();
            Hub.Dispose();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan TenMin = TimeSpan.FromMinutes(10);

    private static void WaitUntil(Func<bool> cond, TimeSpan? timeout = null)
    {
        var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < end, "Timed out waiting for condition.");
            Thread.Sleep(5);
        }
    }

    private static Task<int> Noop(Rig r, WorkPriority p = WorkPriority.Interactive) => r.Worker.RunAsync(_ => 0, p);

    [Fact]
    public async Task RunsOnOneDedicatedThread()
    {
        Thread? configured = null;
        var configureCalls = 0;
        using var r = new Rig(configure: t => { configured = t; configureCalls++; });
        var ids = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await r.Worker.RunAsync(_ => Environment.CurrentManagedThreadId, WorkPriority.Interactive, Ct));
        }
        Assert.Equal(1, configureCalls);
        var workerId = ids[0];
        Assert.All(ids, id => Assert.Equal(workerId, id));
        Assert.NotEqual(Environment.CurrentManagedThreadId, workerId);
        Assert.All(r.Attacher.Threads, id => Assert.Equal(workerId, id));
        Assert.Equal("OutlookWorker", configured!.Name);
        Assert.Equal(workerId, configured.ManagedThreadId);
    }

    [Fact]
    public async Task ResultAndPlainExceptions()
    {
        using var r = new Rig();
        Assert.Equal(42, await r.Worker.RunAsync(_ => 42, WorkPriority.Interactive, Ct));
        var ex = new InvalidOperationException("x");
        var got = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            r.Worker.RunAsync<int>(_ => throw ex, WorkPriority.Interactive, Ct));
        Assert.Same(ex, got);
        Assert.Equal(OutlookConnectionState.Available, r.Worker.State);
        Assert.Equal(0, r.Attacher.ReleaseCalls);
    }

    [Fact]
    public async Task InteractiveFirst()
    {
        using var r = new Rig();
        var order = new List<string>();
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var g = r.Worker.RunAsync(_ => { started.Set(); gate.Wait(Ct); order.Add("G"); return 0; }, WorkPriority.Interactive, Ct);
        started.Wait(Ct);
        Task Q(string n, WorkPriority p) => r.Worker.RunAsync(_ => { order.Add(n); return 0; }, p);
        var all = new[] { g, Q("B1", WorkPriority.Background), Q("B2", WorkPriority.Background),
            Q("I1", WorkPriority.Interactive), Q("I2", WorkPriority.Interactive) };
        gate.Set();
        await Task.WhenAll(all);
        Assert.Equal(["G", "I1", "I2", "B1", "B2"], order);
    }

    [Fact]
    public async Task StartsUnavailableAndFailsFast()
    {
        using var r = new Rig(running: false, initial: TenMin);
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        Assert.Equal(ActivityState.OutlookUnavailable, r.Hub.State);
        Assert.Equal("", r.Hub.Progress.Label);

        var before = r.Attacher.AttachCalls;
        var t = r.Worker.RunAsync(_ => 1, WorkPriority.Interactive, Ct);
        var ex = await Assert.ThrowsAsync<OutlookUnavailableException>(() => t.WaitAsync(TimeSpan.FromSeconds(1), Ct));
        Assert.Equal("Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.", ex.Message);
        Assert.IsAssignableFrom<McpException>(ex);
        Assert.Equal(before + 1, r.Attacher.AttachCalls);
    }

    [Fact]
    public async Task InteractiveAttachesAtOnce()
    {
        using var r = new Rig(running: false, initial: TenMin);
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        r.Attacher.Running = true;
        Assert.Equal(7, await r.Worker.RunAsync(_ => 7, WorkPriority.Interactive, Ct));
        Assert.Equal(OutlookConnectionState.Available, r.Worker.State);
        Assert.Equal(ActivityState.Idle, r.Hub.State);
    }

    [Fact]
    public async Task BackgroundWaitsThenRuns()
    {
        using var r = new Rig(running: false);
        var t = r.Worker.RunAsync(_ => 5, WorkPriority.Background, Ct);
        WaitUntil(() => r.Attacher.AttachCalls >= 3);
        Assert.False(t.IsCompleted);
        r.Attacher.Running = true;
        Assert.Equal(5, await t.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(OutlookConnectionState.Available, r.Worker.State);
    }

    [Theory]
    [InlineData(0x800401E3, WorkPriority.Interactive)]
    [InlineData(0x80010108, WorkPriority.Interactive)]
    [InlineData(0x8001010A, WorkPriority.Interactive)]
    [InlineData(0x800706BA, WorkPriority.Interactive)]
    [InlineData(0x800401E3, WorkPriority.Background)]
    [InlineData(0x80010108, WorkPriority.Background)]
    [InlineData(0x8001010A, WorkPriority.Background)]
    [InlineData(0x800706BA, WorkPriority.Background)]
    public async Task GoneDuringWork(uint hr, WorkPriority priority)
    {
        using var r = new Rig(initial: TenMin);
        var com = new COMException("gone", unchecked((int)hr));
        var ex = await Assert.ThrowsAsync<OutlookUnavailableException>(() =>
            r.Worker.RunAsync<int>(_ => throw com, priority, Ct));
        Assert.Same(com, ex.InnerException);
        Assert.Equal(1, r.Attacher.ReleaseCalls);
        Assert.Equal(OutlookConnectionState.Unavailable, r.Worker.State);
        Assert.Equal(ActivityState.OutlookUnavailable, r.Hub.State);
    }

    [Fact]
    public async Task GoneNested()
    {
        using var r = new Rig(initial: TenMin);
        var ex = await Assert.ThrowsAsync<OutlookUnavailableException>(() =>
            r.Worker.RunAsync<int>(
                _ => throw new InvalidOperationException("wrap", new COMException("x", ComErrors.RPC_E_DISCONNECTED)),
                WorkPriority.Interactive, Ct));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(1, r.Attacher.ReleaseCalls);
        Assert.Equal(OutlookConnectionState.Unavailable, r.Worker.State);
    }

    [Fact]
    public async Task NotGone()
    {
        using var r = new Rig();
        var com = new COMException("fail", unchecked((int)0x80004005));
        var got = await Assert.ThrowsAsync<COMException>(() => r.Worker.RunAsync<int>(_ => throw com, WorkPriority.Interactive, Ct));
        Assert.Same(com, got);
        Assert.Equal(OutlookConnectionState.Available, r.Worker.State);
        Assert.Equal(0, r.Attacher.ReleaseCalls);
    }

    [Fact]
    public async Task ReattachAfterGone()
    {
        using var r = new Rig();
        await Assert.ThrowsAsync<OutlookUnavailableException>(() =>
            r.Worker.RunAsync<int>(_ => throw new COMException("x", ComErrors.RPC_E_DISCONNECTED), WorkPriority.Interactive, Ct));
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available && r.Attacher.AttachCalls >= 2);
        Assert.Equal(2, r.Attacher.AttachCalls);
    }

    [Fact]
    public async Task NudgeAttaches()
    {
        using var r = new Rig(running: false, initial: TenMin);
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        Assert.Equal(1, r.Attacher.AttachCalls);
        r.Attacher.Running = true;
        r.Worker.Nudge();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        var calls = r.Attacher.AttachCalls;
        r.Worker.Nudge();
        await Noop(r);
        Assert.Equal(calls, r.Attacher.AttachCalls);
    }

    [Fact]
    public void ProbeDetectsGone()
    {
        using var r = new Rig(probe: TimeSpan.FromMilliseconds(20), initial: TenMin);
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        r.Attacher.ProbeThrows = new COMException("x", ComErrors.RPC_S_SERVER_UNAVAILABLE);
        r.Attacher.Running = false;
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        Assert.Equal(1, r.Attacher.ReleaseCalls);
    }

    [Fact]
    public void ProbeOtherErrorKeepsInstance()
    {
        using var r = new Rig(probe: TimeSpan.FromMilliseconds(20));
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        r.Attacher.ProbeThrows = new COMException("x", unchecked((int)0x80004005));
        WaitUntil(() => r.Attacher.ProbeCalls >= 3);
        Assert.Equal(OutlookConnectionState.Available, r.Worker.State);
        Assert.Equal(0, r.Attacher.ReleaseCalls);
    }

    [Fact]
    public void AttachThrowsKeepsRetrying()
    {
        var attacher = new FakeAttacher { AttachThrows = new UnauthorizedAccessException() };
        using var hub = new ActivityHub();
        using var w = new ComWorker<FakeApp>(attacher, hub, new ComWorkerOptions
        {
            InitialBackoff = TimeSpan.FromMilliseconds(10),
            MaxBackoff = TimeSpan.FromMilliseconds(40),
        });
        w.Start();
        WaitUntil(() => w.State == OutlookConnectionState.Unavailable);
        WaitUntil(() => attacher.AttachCalls >= 3);
        attacher.AttachThrows = null;
        attacher.Running = true;
        WaitUntil(() => w.State == OutlookConnectionState.Available);
    }

    [Fact]
    public async Task StateChangedSequenceAndThrowingSubscriber()
    {
        using var r = new Rig(running: false, initial: TenMin, start: false);
        var seen = new ConcurrentQueue<OutlookConnectionState>();
        r.Worker.StateChanged += _ => throw new InvalidOperationException("boom");
        r.Worker.StateChanged += s => seen.Enqueue(s);
        r.Worker.Start();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        r.Attacher.Running = true;
        r.Worker.Nudge();
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Available);
        Assert.Equal([OutlookConnectionState.Unavailable, OutlookConnectionState.Available], seen.ToArray());
        Assert.Equal(0, await Noop(r));
    }

    [Fact]
    public async Task Cancellation()
    {
        using var r = new Rig(running: false, initial: TenMin);
        var ran = false;
        using var pre = new CancellationTokenSource();
        pre.Cancel();
        var t1 = r.Worker.RunAsync(_ => { ran = true; return 0; }, WorkPriority.Interactive, pre.Token);
        Assert.True(t1.IsCanceled);

        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        var ran2 = false;
        using var cts = new CancellationTokenSource();
        var t2 = r.Worker.RunAsync(_ => { ran2 = true; return 0; }, WorkPriority.Background, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
        Assert.True(t2.IsCanceled);

        r.Attacher.Running = true;
        r.Worker.Nudge();
        await Noop(r);
        Assert.False(ran);
        Assert.False(ran2);
    }

    [Fact]
    public void ArgumentChecks()
    {
        using var r = new Rig();
        Assert.Throws<ArgumentNullException>(() => { _ = r.Worker.RunAsync<int>(null!, WorkPriority.Interactive, Ct); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = r.Worker.RunAsync(_ => 0, (WorkPriority)5, Ct); });
        using var hub = new ActivityHub();
        Assert.Throws<ArgumentNullException>(() => new ComWorker<FakeApp>(null!, hub));
        Assert.Throws<ArgumentNullException>(() => new ComWorker<FakeApp>(new FakeAttacher(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ComWorker<FakeApp>(new FakeAttacher(), hub, new ComWorkerOptions { ProbeInterval = TimeSpan.Zero }));
        Assert.Throws<InvalidOperationException>(() => r.Worker.Start());
    }

    [Fact]
    public async Task QueuedBeforeStart()
    {
        using var r = new Rig(start: false);
        var t = r.Worker.RunAsync(_ => 3, WorkPriority.Interactive, Ct);
        await Task.Delay(50, Ct);
        Assert.False(t.IsCompleted);
        r.Worker.Start();
        Assert.Equal(3, await t.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task DisposeWhileUnavailable()
    {
        var r = new Rig(running: false, initial: TenMin);
        WaitUntil(() => r.Worker.State == OutlookConnectionState.Unavailable);
        var a = r.Worker.RunAsync(_ => 1, WorkPriority.Background, Ct);
        var b = r.Worker.RunAsync(_ => 2, WorkPriority.Background, Ct);
        r.Worker.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => a);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => b);
        Assert.Equal(ActivityState.Idle, r.Hub.State);
        Assert.Throws<ObjectDisposedException>(() => { _ = r.Worker.RunAsync(_ => 1, WorkPriority.Interactive, Ct); });
        r.Worker.Dispose();
        r.Hub.Dispose();
    }

    [Fact]
    public async Task DisposeReleasesOnWorkerThread()
    {
        var r = new Rig();
        var workerId = await r.Worker.RunAsync(_ => Environment.CurrentManagedThreadId, WorkPriority.Interactive, Ct);
        r.Worker.Dispose();
        Assert.Equal(1, r.Attacher.ReleaseCalls);
        Assert.Equal(workerId, r.Attacher.Threads.Last());
        r.Hub.Dispose();
    }

    [Fact]
    public async Task DisposeWithoutStart()
    {
        var r = new Rig(start: false);
        var t = r.Worker.RunAsync(_ => 1, WorkPriority.Interactive, Ct);
        r.Worker.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => t);
        r.Hub.Dispose();
    }

    [Fact]
    public async Task RunChunked()
    {
        using var r = new Rig();
        var sizes = new List<int>();
        var input = Enumerable.Range(0, 120).ToList();
        var res = await r.Worker.RunChunkedAsync<int, int>(input, (_, c) => { sizes.Add(c.Count); return c.Select(x => x * 2).ToList(); }, Ct);
        Assert.Equal([50, 50, 20], sizes);
        Assert.Equal(input.Select(x => x * 2), res);

        var called = false;
        var empty = await r.Worker.RunChunkedAsync<int, int>([], (_, c) => { called = true; return c; }, Ct);
        Assert.Empty(empty);
        Assert.False(called);

        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = r.Worker.RunChunkedAsync<int, int>(input, (_, c) => c, Ct, chunkSize: 0); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = r.Worker.RunChunkedAsync<int, int>(input, (_, c) => c, Ct, chunkSize: 51); });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            r.Worker.RunChunkedAsync<int, int>(input, (_, _) => null!, Ct));

        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            r.Worker.RunChunkedAsync<int, int>(input, (_, c) =>
            {
                calls++;
                return calls == 2 ? throw new InvalidOperationException("c2") : c;
            }, Ct));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InteractiveBetweenChunks()
    {
        using var r = new Rig();
        var order = new ConcurrentQueue<string>();
        using var gate = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var n = 0;
        var chunked = r.Worker.RunChunkedAsync<int, int>(Enumerable.Range(0, 130).ToList(), (_, c) =>
        {
            var idx = ++n;
            if (idx == 1)
            {
                started.Set();
                gate.Wait();
            }
            order.Enqueue("chunk" + idx);
            return c;
        }, Ct, chunkSize: 50);
        started.Wait(Ct);
        var i = r.Worker.RunAsync(_ => { order.Enqueue("interactive"); return 0; }, WorkPriority.Interactive, Ct);
        gate.Set();
        await chunked;
        await i;
        Assert.Equal(["chunk1", "interactive", "chunk2", "chunk3"], order.ToArray());
    }

    [Fact]
    public async Task ContinuationsOffWorkerThread()
    {
        using var r = new Rig();
        var workerId = await r.Worker.RunAsync(_ => Environment.CurrentManagedThreadId, WorkPriority.Interactive, Ct);
        var after = await Task.Run(async () =>
        {
            await r.Worker.RunAsync(_ => 0, WorkPriority.Interactive);
            return Environment.CurrentManagedThreadId;
        });
        Assert.NotEqual(workerId, after);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Enqueue(formatter(state, exception));
    }

    [Fact]
    public async Task SlowComCallIsLoggedWithNameAndDurationOnly()
    {
        var log = new ListLogger();
        var attacher = new FakeAttacher { Running = true };
        using var hub = new ActivityHub();
        using var w = new ComWorker<FakeApp>(attacher, hub, new ComWorkerOptions
        {
            Logger = log,
            SlowCallThreshold = TimeSpan.FromMilliseconds(50),
            ProbeInterval = TimeSpan.FromHours(1),
        });
        w.Start();
        await w.RunAsync(_ => { Thread.Sleep(200); return 1; }, WorkPriority.Interactive, Ct);
        Assert.Contains(log.Lines, l => l.Contains("took") && l.Contains("COM call"));
        Assert.Contains(log.Lines, l => l.Contains("still running"));

        var before = log.Lines.Count(l => l.Contains("COM call"));
        await w.RunAsync(_ => 2, WorkPriority.Interactive, Ct);
        Assert.Equal(before, log.Lines.Count(l => l.Contains("COM call")));
    }
}
