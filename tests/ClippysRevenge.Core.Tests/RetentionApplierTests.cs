using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class RetentionApplierTests : IDisposable
{
    private static readonly DateTimeOffset Now = ManualTimeProvider.T0;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-ret-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly RetentionApplier _app;

    public RetentionApplierTests()
    {
        _ix = new EmailIndex(Path.Combine(_dir, "index.db"));
        _hub = new ActivityHub(_time);
        _app = new RetentionApplier(_hub, _time);
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

    [Fact]
    public void Fresh_index_records()
    {
        _ix.SetSyncState(new SyncState("k", "Inbox", null, Now.AddYears(-3), null, true));
        var r = _app.Apply("", _ix, 12);
        Assert.Equal(RetentionChangeKind.Recorded, r.Kind);
        Assert.Equal("12", _ix.GetMeta("retention_months"));
        Assert.True(_ix.GetSyncState("k")!.BackfillComplete);
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public void Same_value_is_none()
    {
        _app.Apply("", _ix, 12);
        Assert.Equal(RetentionChangeKind.None, _app.Apply("", _ix, 12).Kind);
        Assert.Equal("12", _ix.GetMeta("retention_months"));
        Assert.Empty(_hub.GetEvents());
    }

    [Fact]
    public void Grow_reopens_folders()
    {
        var c12 = Retention.Cutoff(Now, 12);
        var c24 = Retention.Cutoff(Now, 24);
        _app.Apply("", _ix, 12);
        _ix.SetSyncState(new SyncState("A", "Inbox", null, c12.AddDays(1), null, true));
        _ix.SetSyncState(new SyncState("B", "Sent Items", null, c24.AddDays(-1), null, true));
        _ix.SetSyncState(new SyncState("C", "Drafts", null, null, null, true));
        var r = _app.Apply("", _ix, 24);
        Assert.Equal(RetentionChangeKind.Grew, r.Kind);
        Assert.Equal(1, r.FoldersReopened);
        Assert.False(_ix.GetSyncState("A")!.BackfillComplete);
        Assert.True(_ix.GetSyncState("B")!.BackfillComplete);
        Assert.True(_ix.GetSyncState("C")!.BackfillComplete);
        Assert.Equal("24", _ix.GetMeta("retention_months"));
        var e = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Sync, e.Kind);
        Assert.Equal("Retention changed to 24 months: backfilling older mail", e.Summary);
    }

    [Fact]
    public void Shrink_purges()
    {
        _app.Apply("", _ix, 12);
        _ix.UpsertBatch([
            new() { EntryId = "new", Folder = "Inbox", EventTime = Now.AddMonths(-3) },
            new() { EntryId = "old", Folder = "Inbox", EventTime = Now.AddMonths(-9) }]);
        var r = _app.Apply("help@x.edu", _ix, 6);
        Assert.Equal(RetentionChangeKind.Shrank, r.Kind);
        Assert.Equal(1, r.Purged);
        Assert.NotNull(_ix.Get("new"));
        Assert.Null(_ix.Get("old"));
        Assert.Equal("6", _ix.GetMeta("retention_months"));
        var e = Assert.Single(_hub.GetEvents());
        Assert.Equal(ActivityKind.Purge, e.Kind);
        Assert.Equal("Retention changed to 6 months: purged 1 emails", e.Summary);
        Assert.Equal("help@x.edu", e.Mailbox);
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public void Shrink_own_mailbox_event_has_null_mailbox()
    {
        _app.Apply("", _ix, 12);
        _app.Apply("", _ix, 6);
        Assert.Null(Assert.Single(_hub.GetEvents()).Mailbox);
    }

    [Fact]
    public void Shrink_clamps_oldest_synced()
    {
        _app.Apply("", _ix, 12);
        _ix.SetSyncState(new SyncState("A", "Inbox", null, Now.AddMonths(-12), null, true));
        _ix.SetSyncState(new SyncState("B", "Sent Items", null, Now.AddMonths(-1), null, false));
        _app.Apply("", _ix, 6);
        var a = _ix.GetSyncState("A")!;
        Assert.Equal(Retention.Cutoff(Now, 6).ToUnixTimeSeconds(), a.OldestSynced!.Value.ToUnixTimeSeconds());
        Assert.True(a.BackfillComplete);
        Assert.Equal(Now.AddMonths(-1).ToUnixTimeSeconds(), _ix.GetSyncState("B")!.OldestSynced!.Value.ToUnixTimeSeconds());
        Assert.False(_ix.GetSyncState("B")!.BackfillComplete);
    }

    [Fact]
    public void Shrink_then_grow_reopens()
    {
        _app.Apply("", _ix, 12);
        _ix.SetSyncState(new SyncState("A", "Inbox", null, Now.AddMonths(-12), null, true));
        _app.Apply("", _ix, 6);
        var r = _app.Apply("", _ix, 12);
        Assert.Equal(RetentionChangeKind.Grew, r.Kind);
        Assert.False(_ix.GetSyncState("A")!.BackfillComplete);
    }

    [Fact]
    public void Unparsable_meta_is_recorded()
    {
        _ix.SetMeta("retention_months", "abc");
        Assert.Equal(RetentionChangeKind.Recorded, _app.Apply("", _ix, 12).Kind);
        Assert.Equal("12", _ix.GetMeta("retention_months"));
    }

    [Fact]
    public void Bad_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _app.Apply("", _ix, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _app.Apply("", _ix, 121));
        Assert.Throws<ArgumentNullException>(() => _app.Apply("", null!, 12));
    }

    [Fact]
    public void Cutoff_clamps_and_normalizes_offset()
    {
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero),
            Retention.Cutoff(new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero), 1));
        Assert.Equal(TimeSpan.Zero, Retention.Cutoff(new DateTimeOffset(2026, 3, 31, 2, 0, 0, TimeSpan.FromHours(2)), 1).Offset);
    }

    [Fact]
    public void Reapply_after_shrink_is_none()
    {
        _app.Apply("", _ix, 12);
        _app.Apply("", _ix, 6);
        var r = _app.Apply("", _ix, 6);
        Assert.Equal(RetentionChangeKind.None, r.Kind);
        Assert.Equal(0, r.Purged);
    }
}
