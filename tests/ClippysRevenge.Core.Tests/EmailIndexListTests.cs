using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexListTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-list-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;

    public EmailIndexListTests() => _ix = new EmailIndex(Path.Combine(_dir, "index.db"));

    public void Dispose()
    {
        _ix.Close();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IndexedEmail M(string id, string folder, DateTimeOffset t, bool unread = false) =>
        new() { EntryId = id, Folder = folder, EventTime = t, Unread = unread, Subject = "s" };

    [Fact]
    public void ListEntries_filters_by_folder_newest_first()
    {
        var t = ManualTimeProvider.T0;
        _ix.UpsertBatch([M("a", "Inbox", t.AddDays(-2), true), M("b", "inbox", t.AddDays(-1)), M("c", "Sent Items", t)]);
        var rows = _ix.ListEntries("Inbox");
        Assert.Equal(["b", "a"], rows.Select(r => r.EntryId));
        Assert.True(rows[1].Unread);
        Assert.Equal(t.AddDays(-2).ToUnixTimeSeconds(), rows[1].EventTime.ToUnixTimeSeconds());
    }

    [Fact]
    public void ListEntries_empty_and_bad_args()
    {
        Assert.Empty(_ix.ListEntries("Inbox"));
        Assert.Throws<ArgumentException>(() => _ix.ListEntries(""));
        Assert.ThrowsAny<ArgumentException>(() => _ix.ListEntries(null!));
    }

    [Fact]
    public void ListSyncStates_round_trips_ordered()
    {
        var t = ManualTimeProvider.T0;
        var b = new SyncState("kb", "Sent Items", t, t.AddDays(-1), t.AddDays(-2), true);
        var a = new SyncState("ka", "Inbox", null, null, null, false);
        _ix.SetSyncState(b);
        _ix.SetSyncState(a);
        Assert.Equal([a, b], _ix.ListSyncStates());
    }
}
