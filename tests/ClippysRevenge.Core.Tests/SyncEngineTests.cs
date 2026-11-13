using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class SyncEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = ManualTimeProvider.T0;
    private static readonly DateTimeOffset Cutoff = T0.AddMonths(-12);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-sync-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly FakeMailSource _src = new();
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly SyncEngine _engine;

    public SyncEngineTests()
    {
        _ix = new EmailIndex(Path.Combine(_dir, "index.db"));
        _hub = new ActivityHub(_time);
        _engine = new SyncEngine(_src, _hub, _time);
        _src.Folder("", "Inbox");
    }

    public void Dispose()
    {
        _ix.Close();
        _hub.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Id(int i) => "m" + i.ToString("D3");

    /// <summary>Mail i is i+1 hours before T0 (so m000 is the newest).</summary>
    private static FakeMail Old(int i, string subject = "s") =>
        new(Id(i), T0.AddHours(-(i + 1)), T0.AddHours(-(i + 1)), false, subject);

    private void AddOld(int n, string mailbox = "", string folder = "Inbox")
    {
        for (var i = 0; i < n; i++) _src.Put(mailbox, folder, Old(i));
    }

    private static MailboxPlan Plan(string mailbox = "", int months = 12, params string[] folders) =>
        new(mailbox, folders.Length == 0 ? ["Inbox"] : folders, months);

    private Task<MailboxSyncResult> Run(MailboxPlan? plan = null) =>
        _engine.SyncMailboxAsync(plan ?? Plan(), _ix, TestContext.Current.CancellationToken);

    private Task<MailboxSyncResult> RunWith(CancellationToken ct) =>
        _engine.SyncMailboxAsync(Plan(), _ix, ct);

    private SyncState? State(string path = "Inbox", string mailbox = "") =>
        _ix.GetSyncState(_src.FolderKey(mailbox, path));

    private long Rows() => _ix.Status().TotalEmails;

    [Fact]
    public async Task FirstRun_WritesStateBeforeBackfill()
    {
        AddOld(3);
        SyncState? seen = null;
        _src.BeforeCall = (m, _) =>
        {
            if (m == "ListRangeAsync") seen = State();
            return Task.CompletedTask;
        };
        await Run();
        Assert.NotNull(seen);
        Assert.Equal(T0, seen!.ModifiedWatermark);
        Assert.Equal(T0, seen.OldestSynced);
        Assert.False(seen.BackfillComplete);
    }

    [Fact]
    public async Task FirstRun_BackfillNewestFirstInBatches()
    {
        AddOld(120);
        var firstIds = new List<string>();
        _src.BeforeCall = (m, _) => Task.CompletedTask;
        await Run();
        Assert.Equal([50, 50, 20], _src.ReadBatches);
        var first = (string[])_src.CallsOf("ReadAsync")[0].Args[1]!;
        Assert.Equal(Enumerable.Range(0, 50).Select(Id), first);
        var st = State()!;
        Assert.Equal(Cutoff, st.OldestSynced);
        Assert.True(st.BackfillComplete);
        Assert.Equal(120, Rows());
        Assert.All(Enumerable.Range(0, 120), i => Assert.Equal("Inbox", _ix.Get(Id(i))!.Folder));
        var range = _src.CallsOf("ListRangeAsync").Single();
        Assert.Equal(Cutoff, range.Args[1]);
        Assert.Equal(T0, range.Args[2]);
        _ = firstIds;
    }

    [Fact]
    public async Task Backfill_SkipsOlderThanCutoff()
    {
        _src.Sloppy = true;
        _src.Put("", "Inbox", new FakeMail("old", T0.AddDays(-400), T0.AddDays(-400), false, "x"));
        AddOld(2);
        await Run();
        Assert.Null(_ix.Get("old"));
        Assert.Equal(2, Rows());
    }

    [Fact]
    public async Task Backfill_FailureKeepsProgress()
    {
        AddOld(120);
        _src.ThrowOn("ReadAsync", 3, new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        var st = State()!;
        Assert.Equal(Old(99).EventTime, st.OldestSynced);
        Assert.False(st.BackfillComplete);
        Assert.Equal(100, Rows());
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Backfill_ResumesWhereItStopped()
    {
        AddOld(120);
        _src.ThrowOn("ReadAsync", 3, new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        var before = _src.ReadBatches.Count;
        await Run();
        var range = _src.CallsOf("ListRangeAsync").Last();
        Assert.Equal(Old(99).EventTime, range.Args[2]);
        Assert.Equal(20, _src.ReadBatches.Skip(before).Sum());
        Assert.True(State()!.BackfillComplete);
        Assert.Equal(120, Rows());
    }

    [Fact]
    public async Task Backfill_CancelledBetweenBatches()
    {
        AddOld(120);
        using var cts = new CancellationTokenSource();
        var reads = 0;
        _src.BeforeCall = (m, _) =>
        {
            if (m == "ReadAsync" && ++reads == 2) cts.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunWith(cts.Token));
        Assert.Equal(100, Rows());
        Assert.Equal(Old(99).EventTime, State()!.OldestSynced);
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Backfill_ProgressAndLabel()
    {
        AddOld(120);
        var seen = new List<(ActivityState, ActivityProgress)>();
        _src.BeforeCall = (m, _) =>
        {
            if (m == "ReadAsync") seen.Add((_hub.State, _hub.Progress));
            return Task.CompletedTask;
        };
        await Run();
        Assert.Equal(3, seen.Count);
        Assert.All(seen, s =>
        {
            Assert.Equal(ActivityState.Backfilling, s.Item1);
            Assert.Equal("Inbox (2025-06 → now)", s.Item2.Label);
            Assert.Equal(120, s.Item2.Total);
        });
        Assert.Equal([0, 50, 100], seen.Select(s => s.Item2.Done));
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Backfill_SharedLabel()
    {
        _src.Folder("helpdesk@x.edu", "Inbox");
        AddOld(3, "helpdesk@x.edu");
        string? label = null;
        _src.BeforeCall = (m, _) =>
        {
            if (m == "ReadAsync") label = _hub.Progress.Label;
            return Task.CompletedTask;
        };
        await Run(Plan("helpdesk@x.edu"));
        Assert.Equal("helpdesk@… Inbox (2025-06 → now)", label);
    }

    private async Task CompleteBackfill(int n = 5)
    {
        AddOld(n);
        await Run();
    }

    [Fact]
    public async Task ChangesPass_NewAndEdited()
    {
        await CompleteBackfill();
        _time.Advance(TimeSpan.FromHours(1));
        _src.Put("", "Inbox", new FakeMail("new1", T0.AddMinutes(30), T0.AddMinutes(30), true, "fresh"));
        _src.Put("", "Inbox", Old(0, "edited") with { LastModified = T0.AddMinutes(40) });
        var labels = new List<string>();
        var states = new List<ActivityState>();
        _src.BeforeCall = (m, _) =>
        {
            if (m == "ReadAsync") { labels.Add(_hub.Progress.Label); states.Add(_hub.State); }
            return Task.CompletedTask;
        };
        var before = _src.ReadBatches.Count;
        await Run();
        Assert.Equal(2, _src.ReadBatches.Skip(before).Sum());
        Assert.Equal("edited", _ix.Get(Id(0))!.Subject);
        Assert.NotNull(_ix.Get("new1"));
        Assert.Equal(T0.AddMinutes(40), State()!.ModifiedWatermark);
        Assert.Equal(["Inbox +2"], labels);
        Assert.Equal([ActivityState.Syncing], states);
    }

    [Fact]
    public async Task ChangesPass_AscendingWatermarkPerBatch()
    {
        _src.Put("", "Inbox", Old(0));
        await Run();
        _time.Advance(TimeSpan.FromHours(3));
        for (var i = 1; i <= 120; i++)
            _src.Put("", "Inbox", new FakeMail(Id(i + 500), T0.AddMinutes(i), T0.AddMinutes(i), false, "c"));
        var reads = _src.CallsOf("ReadAsync").Count;
        _src.ThrowOn("ReadAsync", reads + 2, new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        Assert.Equal(T0.AddMinutes(50), State()!.ModifiedWatermark);
        var firstIds = (string[])_src.CallsOf("ReadAsync")[reads].Args[1]!;
        Assert.Equal(Enumerable.Range(1, 50).Select(i => Id(i + 500)), firstIds);
    }

    [Fact]
    public async Task ChangesPass_SteadyStateNoReads()
    {
        await CompleteBackfill();
        await Run();
        _src.Sloppy = true;
        var reads = _src.CallsOf("ReadAsync").Count;
        var events = _hub.GetEvents().Count;
        var syncingSeen = false;
        _src.BeforeCall = (_, _) =>
        {
            if (_hub.State == ActivityState.Syncing) syncingSeen = true;
            return Task.CompletedTask;
        };
        await Run();
        Assert.Equal(reads, _src.CallsOf("ReadAsync").Count);
        Assert.Equal(events, _hub.GetEvents().Count);
        Assert.False(syncingSeen);
    }

    [Fact]
    public async Task ChangesPass_UnreadOnly()
    {
        var m = new FakeMail("u1", T0, T0.AddHours(-1), true, "s");
        _src.Put("", "Inbox", m);
        await Run();
        Assert.True(_ix.Get("u1")!.Unread);
        var reads = _src.CallsOf("ReadAsync").Count;
        _src.Put("", "Inbox", m with { Unread = false });
        var r = await Run();
        Assert.Equal(reads, _src.CallsOf("ReadAsync").Count);
        Assert.False(_ix.Get("u1")!.Unread);
        Assert.Equal(1, r.Folders.Single().UnreadUpdated);
    }

    [Fact]
    public async Task ChangesPass_OldMailMovedIn()
    {
        await CompleteBackfill();
        _time.Advance(TimeSpan.FromMinutes(10));
        _src.Put("", "Inbox", new FakeMail("moved", T0.AddMinutes(5), T0.AddDays(-200), false, "m"));
        await Run();
        Assert.NotNull(_ix.Get("moved"));
    }

    [Fact]
    public async Task Folder_SetFromPlan()
    {
        _src.Folder("", "Inbox\\Projects");
        _src.Put("", "Inbox\\Projects", Old(0));
        await Run(Plan("", 12, "Inbox\\Projects"));
        Assert.Equal("Inbox\\Projects", _ix.Get(Id(0))!.Folder);
    }

    [Fact]
    public async Task MissingFolder_Skipped()
    {
        AddOld(2);
        var r = await Run(Plan("", 12, "Inbox", "Nope"));
        Assert.Equal(2, Rows());
        var nope = r.Folders.Single(f => f.Folder == "Nope");
        Assert.True(nope.Missing);
        Assert.False(r.Folders.Single(f => f.Folder == "Inbox").Missing);
    }

    [Fact]
    public async Task NotMounted_NoWrites()
    {
        AddOld(2);
        _src.Box("").Mounted = false;
        var r = await Run();
        Assert.True(r.NotMounted);
        Assert.Empty(r.Folders);
        Assert.Null(_ix.GetMeta("store_id"));
        Assert.Null(State());
        Assert.Equal(0, Rows());
    }

    [Fact]
    public async Task Meta_Written()
    {
        AddOld(2);
        _src.Box("").Smtp = "me@x.edu";
        await Run();
        Assert.Equal("me@x.edu", _ix.GetMeta("mailbox_smtp"));
        Assert.Equal("store-", _ix.GetMeta("store_id"));
        Assert.Equal("12", _ix.GetMeta("retention_months"));
        Assert.Equal(T0.ToUnixTimeSeconds().ToString(), _ix.GetMeta("last_sync"));
        Assert.Equal(T0, _ix.Status().LastSync);
    }

    [Fact]
    public async Task RetentionIncrease_BackfillsAgain()
    {
        _src.Put("", "Inbox", new FakeMail("two", T0.AddMonths(-2), T0.AddMonths(-2), false, "a"));
        _src.Put("", "Inbox", new FakeMail("recent", T0.AddDays(-20), T0.AddDays(-20), false, "b"));
        await Run(Plan("", 1));
        Assert.Null(_ix.Get("two"));
        Assert.NotNull(_ix.Get("recent"));
        Assert.True(State()!.BackfillComplete);

        bool? completeDuring = null;
        _src.BeforeCall = (m, _) =>
        {
            if (m == "ListRangeAsync") completeDuring = State()!.BackfillComplete;
            return Task.CompletedTask;
        };
        await Run(Plan("", 3));
        var range = _src.CallsOf("ListRangeAsync").Last();
        Assert.Equal(T0.AddMonths(-3), range.Args[1]);
        Assert.Equal(T0.AddMonths(-1), range.Args[2]);
        Assert.NotNull(_ix.Get("two"));
        Assert.True(State()!.BackfillComplete);
        Assert.Equal(false, completeDuring);
    }

    [Fact]
    public async Task Purge_RemovesOldRows()
    {
        _ix.UpsertBatch([
            new IndexedEmail { EntryId = "o1", Folder = "Inbox", EventTime = Cutoff.AddDays(-5) },
            new IndexedEmail { EntryId = "o2", Folder = "Inbox", EventTime = Cutoff.AddDays(-9) },
        ]);
        AddOld(1);
        var r = await Run();
        Assert.Equal(2, r.Purged);
        Assert.Null(_ix.Get("o1"));
        var ev = Assert.Single(_hub.GetEvents(), e => e.Kind == ActivityKind.Purge);
        Assert.Equal("removed 2 emails older than 2025-06-15", ev.Summary);
        Assert.Equal("", ev.Mailbox);
    }

    [Fact]
    public async Task Purge_NothingOld_NoEvent()
    {
        AddOld(2);
        await Run();
        Assert.DoesNotContain(_hub.GetEvents(), e => e.Kind == ActivityKind.Purge);
    }

    [Fact]
    public async Task SyncEvent_Text()
    {
        _src.Folder("", "Sent Items");
        for (var i = 0; i < 3; i++) _src.Put("", "Inbox", Old(i));
        for (var i = 10; i < 12; i++) _src.Put("", "Sent Items", Old(i));
        await Run(Plan("", 12, "Inbox", "Sent Items"));
        var ev = Assert.Single(_hub.GetEvents(), e => e.Kind == ActivityKind.Sync);
        Assert.Equal("Inbox +3, Sent +2 (0.0 s)", ev.Summary);
        Assert.Equal("", ev.Mailbox);
    }

    [Fact]
    public async Task BackfillEvents()
    {
        AddOld(120);
        await Run();
        var texts = _hub.GetEvents().Where(e => e.Kind == ActivityKind.Backfill).Select(e => e.Summary).ToList();
        Assert.Equal([
            "Inbox: backfill started, 120 emails to index (2025-06 → now)",
            "Inbox: backfill complete",
        ], texts);
    }

    [Fact]
    public async Task BackfillFinished_Flag()
    {
        AddOld(3);
        Assert.True((await Run()).BackfillFinished);
        Assert.False((await Run()).BackfillFinished);
    }

    [Fact]
    public async Task Unavailable_Propagates()
    {
        AddOld(3);
        _src.ThrowOn("ListChangedAsync", 1, new MailSourceUnavailableException("x"));
        await Assert.ThrowsAsync<MailSourceUnavailableException>(() => Run());
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task DuplicateFolderInPlan()
    {
        AddOld(2);
        await Run(Plan("", 12, "Inbox", "Inbox", "inbox"));
        Assert.Single(_src.CallsOf("GetFolderAsync"));
    }
}
