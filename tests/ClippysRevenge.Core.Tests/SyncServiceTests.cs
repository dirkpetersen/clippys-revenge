using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class SyncServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = ManualTimeProvider.T0;
    private const string A = "a@x.edu";
    private const string B = "b@x.edu";
    private const string C = "c@x.edu";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-sync-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly BalloonCenter _balloons;
    private readonly FakeMailSource _src = new();
    private readonly Settings _settings = new();
    private readonly List<SyncCycleResult> _cycles = [];
    private readonly SemaphoreSlim _cycleSem = new(0);
    private int _consumed;
    private MailboxIndexes? _indexes;
    private SyncService? _svc;

    public SyncServiceTests()
    {
        _hub = new ActivityHub(_time);
        _balloons = new BalloonCenter(_time);
        _settings.IndexedFolders = ["Inbox"];
        _src.Folder("", "Inbox");
    }

    public void Dispose()
    {
        if (_svc is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { _svc.StopAsync(cts.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        _indexes?.Dispose();
        _hub.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Id(int i) => "m" + i.ToString("D3");

    private static FakeMail Old(int i) => new(Id(i), T0.AddHours(-(i + 1)), T0.AddHours(-(i + 1)), false, "s");

    private void AddMails(int n, string mailbox = "", string folder = "Inbox", int start = 0)
    {
        _src.Folder(mailbox, folder);
        for (var i = start; i < start + n; i++) _src.Put(mailbox, folder, Old(i));
    }

    private void AddShared(string smtp, bool enabled = true)
    {
        _settings.SharedMailboxes.Add(new SharedMailbox { Smtp = smtp, Enabled = enabled, IndexedFolders = ["Inbox"] });
        _src.Folder(smtp, "Inbox");
    }

    private SyncService Make()
    {
        _indexes = new MailboxIndexes(_dir, _settings.SharedMailboxes.Where(m => m.Enabled).Select(m => m.Smtp));
        _svc = new SyncService(_src, _indexes, () => _settings, _hub, _balloons, _time);
        _svc.CycleFinished += r =>
        {
            lock (_cycles) _cycles.Add(r);
            _cycleSem.Release();
        };
        return _svc;
    }

    private async Task<SyncService> Start()
    {
        var s = Make();
        await s.StartAsync(CancellationToken.None);
        return s;
    }

    private async Task<SyncCycleResult> WaitCycle()
    {
        if (!await _cycleSem.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("no cycle finished");
        lock (_cycles) return _cycles[_consumed++];
    }

    private static async Task Settle() => await Task.Delay(250);

    private int CycleCount()
    {
        lock (_cycles) return _cycles.Count;
    }

    private sealed class Gate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public async Task Block() { _entered.TrySetResult(); await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        public void Release() => _release.TrySetResult();
    }

    /// <summary>Blocks the nth call of the method once.</summary>
    private Gate BlockCall(string method, int n)
    {
        var gate = new Gate();
        var seen = 0;
        _src.BeforeCall = async (m, _) =>
        {
            if (m == method && Interlocked.Increment(ref seen) == n) await gate.Block();
        };
        return gate;
    }

    [Fact]
    public async Task Start_RunsFirstCycleImmediately()
    {
        AddMails(3);
        await Start();
        var r = await WaitCycle();
        Assert.False(r.Aborted);
        Assert.Equal(3, _indexes!.For("").Status().TotalEmails);
    }

    [Fact]
    public async Task Order_OwnThenEnabledShared()
    {
        AddShared(A);
        AddShared(B, enabled: false);
        AddShared(C);
        await Start();
        await WaitCycle();
        Assert.Equal(["", A, C], _src.CallsOf("OpenMailboxAsync").Select(c => (string)c.Args[0]!));
        Assert.True(File.Exists(MailboxIndexes.PathFor(_dir, C)));
        Assert.False(File.Exists(MailboxIndexes.PathFor(_dir, B)));
    }

    [Fact]
    public async Task Interval_WaitsThenRuns()
    {
        await Start();
        await WaitCycle();
        _time.Advance(TimeSpan.FromSeconds(299));
        await Settle();
        Assert.Equal(1, CycleCount());
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitCycle();
    }

    [Fact]
    public async Task SyncNow_WakesAndCoalesces()
    {
        var svc = await Start();
        await WaitCycle();
        svc.SyncNow();
        await WaitCycle();

        var gate = BlockCall("OpenMailboxAsync", 1);
        svc.SyncNow();
        await gate.Entered;
        svc.SyncNow();
        svc.SyncNow();
        svc.SyncNow();
        gate.Release();
        await WaitCycle(); // cycle 3
        await WaitCycle(); // cycle 4, from the collapsed signals
        await Settle();
        Assert.Equal(4, CycleCount());
    }

    [Fact]
    public async Task Pause_StopsAndResumeRuns()
    {
        var svc = await Start();
        await WaitCycle();
        svc.Pause();
        Assert.True(svc.IsPaused);
        Assert.Equal(ActivityState.Paused, _hub.State);
        Assert.True(svc.GetStats().Paused);
        Assert.Null(svc.GetStats().NextSync);
        svc.SyncNow();
        _time.Advance(TimeSpan.FromHours(1));
        await Settle();
        Assert.Equal(1, CycleCount());
        svc.Resume();
        await WaitCycle();
        Assert.NotEqual(ActivityState.Paused, _hub.State);
        Assert.False(svc.IsPaused);
    }

    [Fact]
    public async Task Pause_MidCycle_StopsAtBatchBoundary()
    {
        AddMails(120);
        var gate = BlockCall("ReadAsync", 2);
        var svc = await Start();
        await gate.Entered;
        svc.Pause();
        gate.Release();
        var r = await WaitCycle();
        Assert.True(r.Aborted);
        Assert.Equal(100, _indexes!.For("").Status().TotalEmails);
        Assert.Equal(2, _src.ReadBatches.Count);
        svc.Resume();
        var r2 = await WaitCycle();
        Assert.False(r2.Aborted);
        Assert.Equal(120, _indexes.For("").Status().TotalEmails);
        Assert.Equal(20, _src.ReadBatches[^1]);
    }

    [Fact]
    public async Task SettingsChanged_ReplansFolders()
    {
        var svc = await Start();
        await WaitCycle();
        _src.Folder("", "Archive");
        _settings.IndexedFolders = ["Inbox", "Archive"];
        svc.NotifySettingsChanged();
        await WaitCycle();
        Assert.Contains(_src.CallsOf("GetFolderAsync"), c => (string)c.Args[0]! == "Archive");
    }

    [Fact]
    public async Task Rebuild_Own()
    {
        AddMails(120);
        var svc = await Start();
        await WaitCycle();
        svc.Pause();
        await svc.RebuildAsync("", TestContext.Current.CancellationToken);
        var ix = _indexes!.For("");
        Assert.Equal(0, ix.Status().TotalEmails);
        Assert.Null(ix.GetSyncState(_src.FolderKey("", "Inbox")));
        Assert.Contains(_hub.GetEvents(), e => e.Summary == "index deleted for rebuild" && e.Mailbox == "");
        svc.Resume();
        var r = await WaitCycle();
        Assert.Equal(120, _indexes.For("").Status().TotalEmails);
        Assert.True(r.Mailboxes[0].BackfillFinished);
    }

    [Fact]
    public async Task Rebuild_All_ResetsShared()
    {
        AddMails(5);
        AddShared(A);
        AddMails(4, A);
        var svc = await Start();
        await WaitCycle();
        Assert.Equal(4, _indexes!.For(A).Status().TotalEmails);
        svc.Pause();
        await svc.RebuildAsync("all", TestContext.Current.CancellationToken);
        Assert.Equal(0, _indexes.For("").Status().TotalEmails);
        Assert.Equal(0, _indexes.For(A).Status().TotalEmails);
    }

    [Fact]
    public async Task Rebuild_DuringCycle_CancelsAndWaits()
    {
        AddMails(120);
        var gate = BlockCall("ReadAsync", 1);
        var svc = await Start();
        await gate.Entered;
        var rebuild = svc.RebuildAsync("", TestContext.Current.CancellationToken);
        await Settle();
        Assert.False(rebuild.IsCompleted);
        gate.Release();
        var first = await WaitCycle();
        Assert.True(first.Aborted);
        await rebuild.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(_hub.GetEvents(), e => e.Summary == "index deleted for rebuild");
        var second = await WaitCycle();
        Assert.True(second.Mailboxes[0].BackfillFinished);
        Assert.Equal(120, _indexes!.For("").Status().TotalEmails);
        // The second cycle started from scratch: its backfill listed up to "now".
        Assert.Equal(T0, _src.CallsOf("ListRangeAsync").Last().Args[2]);
    }

    [Fact]
    public async Task Rebuild_UnknownShared_Throws()
    {
        var svc = await Start();
        await WaitCycle();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RebuildAsync("x@y.org", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_IsolatedPerMailbox()
    {
        AddMails(3);
        AddShared(A);
        AddMails(2, A);
        _src.ThrowOn("ListChangedAsync", 2, new InvalidOperationException("boom"));
        var svc = await Start();
        await WaitCycle();
        Assert.Equal(3, _indexes!.For("").Status().TotalEmails);
        var ev = Assert.Single(_hub.GetEvents(), e => e.Kind == ActivityKind.Error);
        Assert.Equal("sync failed: boom", ev.Summary);
        Assert.Equal(A, ev.Mailbox);
        Assert.Equal(ActivityState.Error, _hub.State);
        Assert.Equal("sync failed for a@…", _hub.Progress.Label);
        svc.SyncNow();
        await WaitCycle();
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Error_ScopeNotDuplicated()
    {
        AddShared(A);
        AddMails(2, A);
        _src.ThrowOn("ListChangedAsync", 2, new InvalidOperationException("boom"));
        _src.ThrowOn("ListChangedAsync", 4, new InvalidOperationException("boom"));
        var svc = await Start();
        await WaitCycle();
        svc.SyncNow();
        await WaitCycle();
        Assert.Equal(ActivityState.Error, _hub.State);
        svc.SyncNow();
        await WaitCycle();
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Unavailable_AbortsQuietly()
    {
        var svc = await Start();
        await WaitCycle();
        Assert.Equal(T0, svc.GetStats().LastSync);
        _time.Advance(TimeSpan.FromSeconds(10));
        _src.ThrowOn("OpenMailboxAsync", 2, new MailSourceUnavailableException("gone"));
        svc.SyncNow();
        var r = await WaitCycle();
        Assert.True(r.Aborted);
        Assert.DoesNotContain(_hub.GetEvents(), e => e.Kind == ActivityKind.Error);
        Assert.NotEqual(ActivityState.Error, _hub.State);
        Assert.Equal(T0, svc.GetStats().LastSync);
        svc.SyncNow();
        var r3 = await WaitCycle();
        Assert.False(r3.Aborted);
        Assert.Equal(T0.AddSeconds(10), svc.GetStats().LastSync);
    }

    private int Count(string summary) => _hub.GetEvents().Count(e => e.Summary == summary);

    [Fact]
    public async Task NotMounted_EventOncePerEpisode()
    {
        AddShared(A);
        _src.Box(A).Mounted = false;
        var svc = await Start();
        await WaitCycle();
        svc.SyncNow();
        await WaitCycle();
        Assert.Equal(1, Count("a@x.edu is not in Outlook; skipped"));
        Assert.DoesNotContain(_hub.GetEvents(), e => e.Summary.StartsWith("sync failed", StringComparison.Ordinal));
        Assert.NotEqual(ActivityState.Error, _hub.State);
        _src.Box(A).Mounted = true;
        svc.SyncNow();
        await WaitCycle();
        _src.Box(A).Mounted = false;
        svc.SyncNow();
        await WaitCycle();
        Assert.Equal(2, Count("a@x.edu is not in Outlook; skipped"));
    }

    [Fact]
    public async Task MissingFolder_EventOncePerEpisode()
    {
        _settings.IndexedFolders = ["Inbox", "Nope"];
        var svc = await Start();
        await WaitCycle();
        svc.SyncNow();
        await WaitCycle();
        const string text = "folder \"Nope\" not found; skipped";
        Assert.Equal(1, Count(text));
        _src.Folder("", "Nope");
        svc.SyncNow();
        await WaitCycle();
        _src.Box("").Folders.Remove("Nope");
        svc.SyncNow();
        await WaitCycle();
        Assert.Equal(2, Count(text));
    }

    [Fact]
    public async Task Stats()
    {
        AddMails(3);
        AddShared(A);
        AddMails(2, A);
        var svc = Make();
        Assert.Null(svc.GetStats().LastSync);
        await svc.StartAsync(CancellationToken.None);
        await WaitCycle();
        var s = svc.GetStats();
        Assert.Equal(5, s.EmailCount);
        Assert.Equal(T0, s.LastSync);
        Assert.Equal(T0.AddSeconds(300), s.NextSync);

        var gate = BlockCall("OpenMailboxAsync", 1);
        svc.SyncNow();
        await gate.Entered;
        Assert.Null(svc.GetStats().NextSync);
        gate.Release();
        await WaitCycle();
    }

    [Fact]
    public async Task BackfillBalloon_OncePerMailbox()
    {
        AddMails(120);
        var shown = new List<Balloon>();
        _balloons.Shown += b => { lock (shown) shown.Add(b); };
        var svc = await Start();
        await WaitCycle();
        lock (shown)
            Assert.Equal(["It looks like you're trying to find an email. I finished indexing your mailbox: 120 emails."],
                shown.Select(b => b.Text));
        _time.Advance(TimeSpan.FromMinutes(5));
        svc.SyncNow();
        await WaitCycle();
        lock (shown) Assert.Single(shown);
    }

    [Fact]
    public async Task Stop_CompletesAndCleansUp()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _src.BeforeCall = async (m, ct) =>
        {
            if (m == "OpenMailboxAsync")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var svc = Make();
        await svc.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StopAsync(cts.Token);
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Stop_WhilePaused_DisposesPausedScope()
    {
        var svc = await Start();
        await WaitCycle();
        svc.Pause();
        Assert.Equal(ActivityState.Paused, _hub.State);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StopAsync(cts.Token);
        Assert.Equal(ActivityState.Idle, _hub.State);
    }
}
