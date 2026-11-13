using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class MailboxReconcilerTests : IDisposable
{
    private static readonly DateTimeOffset Now = ManualTimeProvider.T0;
    private static readonly DateTimeOffset Cutoff = Retention.Cutoff(Now, 12);
    private static readonly ReconcileFolder Inbox = new("fk-inbox", "Inbox");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-rec-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private readonly ManualTimeProvider _time = new();
    private readonly ActivityHub _hub;
    private readonly FakeSource _src;
    private readonly MailboxReconciler _rec;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public MailboxReconcilerTests()
    {
        _ix = new EmailIndex(Path.Combine(_dir, "index.db"));
        _hub = new ActivityHub(_time);
        _src = new FakeSource(_hub);
        _rec = new MailboxReconciler(_src, _hub, _time);
        Seed();
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

    private void Seed(bool complete = true, DateTimeOffset? last = null) =>
        _ix.SetSyncState(new SyncState("fk-inbox", "Inbox", Now.AddDays(-1), Cutoff, last, complete));

    private static IndexedEmail M(string id, string folder = "Inbox", double days = 10, bool unread = false, string? body = null) =>
        new() { EntryId = id, Folder = folder, EventTime = Cutoff.AddDays(days), Unread = unread, Subject = "s", Body = body };

    private Task<ReconcileResult> Run(string mailbox = "", CancellationToken? ct = null) =>
        _rec.ReconcileIfDueAsync(mailbox, _ix, Inbox, Cutoff, ct ?? _ct);

    private void Full()
    {
        _ix.UpsertBatch([M("a", body: "alphaword"), M("b", body: "bravoword"), M("c")]);
        _src.Listed = [new("a", true), new("c", false), new("d", false)];
        _src.Readable["d"] = M("d", body: "deltaword");
    }

    [Fact]
    public async Task Not_due_without_state()
    {
        var r = await _rec.ReconcileIfDueAsync("", _ix, new("other", "Drafts"), Cutoff, _ct);
        Assert.False(r.Ran);
        Assert.Equal(0, _src.ListCalls);
    }

    [Fact]
    public async Task Not_due_when_backfill_incomplete()
    {
        Seed(complete: false);
        Assert.False((await Run()).Ran);
        Assert.Equal(0, _src.ListCalls);
    }

    [Fact]
    public async Task Due_boundary_is_24h()
    {
        Seed(last: Now.AddHours(-23));
        Assert.False((await Run()).Ran);
        Seed(last: Now.AddHours(-24));
        Assert.True((await Run()).Ran);
    }

    [Fact]
    public async Task Full_run()
    {
        Full();
        var r = await Run();
        Assert.True(r.Ran);
        Assert.Equal((1, 1, 1, 0), (r.Deleted, r.Added, r.UnreadUpdated, r.Skipped));
        Assert.Null(_ix.Get("b"));
        Assert.Equal("Inbox", _ix.Get("d")!.Folder);
        Assert.True(_ix.Get("a")!.Unread);
        Assert.Empty(_ix.Search(new SearchFilter { Query = "bravoword" }));
        Assert.Single(_ix.Search(new SearchFilter { Query = "deltaword" }));
    }

    [Fact]
    public async Task Source_called_with_cutoff_mailbox_folder()
    {
        Full();
        await Run();
        Assert.Equal((Cutoff, "", Inbox), (_src.Since, _src.Mailbox, _src.Folder));
    }

    [Fact]
    public async Task Last_reconcile_written_other_fields_kept()
    {
        Full();
        await Run();
        var s = _ix.GetSyncState("fk-inbox")!;
        Assert.Equal(Now.ToUnixTimeSeconds(), s.LastReconcile!.Value.ToUnixTimeSeconds());
        Assert.Equal(Now.AddDays(-1).ToUnixTimeSeconds(), s.ModifiedWatermark!.Value.ToUnixTimeSeconds());
        Assert.Equal(Cutoff.ToUnixTimeSeconds(), s.OldestSynced!.Value.ToUnixTimeSeconds());
        Assert.True(s.BackfillComplete);
        Assert.Equal("Inbox", s.FolderPath);
    }

    [Fact]
    public async Task Second_call_not_due()
    {
        Full();
        await Run();
        var calls = _src.ListCalls;
        Assert.False((await Run()).Ran);
        Assert.Equal(calls, _src.ListCalls);
    }

    [Fact]
    public async Task Batches_of_50()
    {
        _src.Listed = Enumerable.Range(0, 120).Select(i => new OutlookEntry("n" + i.ToString("D3"), false)).ToList();
        foreach (var e in _src.Listed) _src.Readable[e.EntryId] = M(e.EntryId);
        var r = await Run();
        Assert.Equal(120, r.Added);
        Assert.Equal([50, 50, 20], _src.Batches.Select(b => b.Count));
        Assert.Equal("n000", _src.Batches[0][0]);
        Assert.Equal("n119", _src.Batches[2][^1]);
    }

    [Fact]
    public async Task Unreadable_counted_and_reported()
    {
        _src.Listed = [new("x", false), new("y", false), new("z", false)];
        _src.Readable["x"] = M("x");
        var r = await Run();
        Assert.Equal((1, 2), (r.Added, r.Skipped));
        Assert.EndsWith("2 unreadable", Assert.Single(_hub.GetEvents()).Summary);
    }

    [Fact]
    public async Task Unrequested_email_ignored()
    {
        _src.Listed = [new("x", false)];
        _src.Extra = M("stray");
        var r = await Run();
        Assert.Equal(0, r.Added);
        Assert.Null(_ix.Get("stray"));
    }

    [Fact]
    public async Task Cancellation_keeps_work_and_retries()
    {
        _src.Listed = Enumerable.Range(0, 60).Select(i => new OutlookEntry("n" + i.ToString("D3"), false)).ToList();
        foreach (var e in _src.Listed) _src.Readable[e.EntryId] = M(e.EntryId);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        _src.CancelOnRead = cts;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(ct: cts.Token));
        Assert.Equal(50, _ix.Status().TotalEmails);
        Assert.Null(_ix.GetSyncState("fk-inbox")!.LastReconcile);
        _src.CancelOnRead = null;
        var r = await Run();
        Assert.Equal(10, r.Added);
        Assert.Equal(60, _ix.Status().TotalEmails);
    }

    [Fact]
    public async Task List_failure_propagates()
    {
        _ix.UpsertBatch([M("a")]);
        _src.ListError = new InvalidOperationException("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run());
        Assert.NotNull(_ix.Get("a"));
        Assert.Null(_ix.GetSyncState("fk-inbox")!.LastReconcile);
    }

    [Fact]
    public async Task Suppressed_deletes()
    {
        _ix.UpsertBatch(Enumerable.Range(0, 20).Select(i => M("x" + i)).ToList());
        var r = await Run();
        Assert.True(r.DeletesSuppressed);
        Assert.Equal(0, r.Deleted);
        Assert.Equal(20, _ix.Status().TotalEmails);
        Assert.NotNull(_ix.GetSyncState("fk-inbox")!.LastReconcile);
        var e = Assert.Single(_hub.GetEvents());
        Assert.Equal("Reconcile Inbox: Outlook listed no items, kept 20 indexed emails", e.Summary);
    }

    [Fact]
    public async Task Event_text_and_mailbox_tag()
    {
        Full();
        await Run();
        var e = Assert.Single(_hub.GetEvents());
        Assert.Equal("Reconciled Inbox: 1 removed, 1 added, 1 read-state updated", e.Summary);
        Assert.Equal(ActivityKind.Sync, e.Kind);
        Assert.Null(e.Mailbox);
    }

    [Fact]
    public async Task Event_has_shared_mailbox_tag()
    {
        Full();
        await Run("help@x.edu");
        Assert.Equal("help@x.edu", Assert.Single(_hub.GetEvents()).Mailbox);
    }

    [Fact]
    public async Task No_changes_no_event_but_state_written()
    {
        _ix.UpsertBatch([M("a")]);
        _src.Listed = [new("a", false)];
        var r = await Run();
        Assert.True(r.Ran);
        Assert.Empty(_hub.GetEvents());
        Assert.NotNull(_ix.GetSyncState("fk-inbox")!.LastReconcile);
    }

    [Fact]
    public async Task Reports_syncing_activity()
    {
        Full();
        await Run();
        Assert.Equal(ActivityState.Syncing, _src.StateDuringList);
        Assert.Equal("Reconciling Inbox", _src.LabelDuringList);
        Assert.Equal(ActivityState.Idle, _hub.State);
    }

    [Fact]
    public async Task Other_folders_untouched()
    {
        _ix.UpsertBatch([M("s1", "Sent Items")]);
        _src.Listed = [];
        await Run();
        Assert.NotNull(_ix.Get("s1"));
    }

    private sealed class FakeSource(ActivityHub hub) : IReconcileSource
    {
        public List<OutlookEntry> Listed { get; set; } = [];
        public Dictionary<string, IndexedEmail> Readable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<List<string>> Batches { get; } = [];
        public int ListCalls { get; private set; }
        public DateTimeOffset Since { get; private set; }
        public string? Mailbox { get; private set; }
        public ReconcileFolder? Folder { get; private set; }
        public Exception? ListError { get; set; }
        public IndexedEmail? Extra { get; set; }
        public CancellationTokenSource? CancelOnRead { get; set; }
        public ActivityState StateDuringList { get; private set; }
        public string? LabelDuringList { get; private set; }

        public Task<IReadOnlyList<OutlookEntry>> ListEntryIdsAsync(string mailbox, ReconcileFolder folder, DateTimeOffset since, CancellationToken ct)
        {
            ListCalls++;
            Mailbox = mailbox;
            Folder = folder;
            Since = since;
            StateDuringList = hub.State;
            LabelDuringList = hub.Progress.Label;
            if (ListError is not null) throw ListError;
            return Task.FromResult<IReadOnlyList<OutlookEntry>>(Listed);
        }

        public Task<IReadOnlyList<IndexedEmail>> ReadForIndexAsync(string mailbox, ReconcileFolder folder, IReadOnlyList<string> entryIds, CancellationToken ct)
        {
            Batches.Add([.. entryIds]);
            var list = entryIds.Where(Readable.ContainsKey).Select(i => Readable[i]).ToList();
            if (Extra is not null) list.Add(Extra);
            CancelOnRead?.Cancel();
            return Task.FromResult<IReadOnlyList<IndexedEmail>>(list);
        }
    }
}
