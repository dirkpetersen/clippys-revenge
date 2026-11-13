using ClippysRevenge.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-ixm-" + Guid.NewGuid().ToString("N"));
    private string DbFile => Path.Combine(_dir, "index.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    private static IndexedEmail Mail(int i, string folder = "Inbox", string body = "common") => new()
    {
        EntryId = "e" + i,
        Folder = folder,
        Subject = "subject " + i,
        Body = body + " word" + i,
        EventTime = Base.AddDays(i),
        Attachments = [new AttachmentInfo(0, "f" + i + ".pdf", 10, false)],
    };

    [Fact]
    public void Purge_RemovesOldRowsAndAttachments_KeepsFts()
    {
        var ix = new EmailIndex(DbFile);
        ix.UpsertBatch(Enumerable.Range(0, 10).Select(i => Mail(i)).ToList());
        var removed = ix.PurgeOlderThan(Base.AddDays(5)); // 5 of 10 -> vacuum
        Assert.Equal(5, removed);
        Assert.Null(ix.Get("e4"));
        Assert.NotNull(ix.Get("e5"));
        Assert.Single(ix.Search(new SearchFilter { Query = "word7" }));
        Assert.Empty(ix.Search(new SearchFilter { Query = "word2" }));
        Assert.Equal(5, ix.Search(new SearchFilter { Query = "common" }).Count);
        Assert.Empty(ix.Search(new SearchFilter { AttachmentName = "f2.pdf" }));
        Assert.Single(ix.Search(new SearchFilter { AttachmentName = "f7.pdf" }));
        // Index still writable and searchable after VACUUM.
        ix.UpsertBatch([Mail(50, body: "fresh")]);
        Assert.Single(ix.Search(new SearchFilter { Query = "fresh" }));
        using var c = ix.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO emails_fts(emails_fts, rank) VALUES('integrity-check', 1)";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Purge_SmallFraction_StillWorks()
    {
        var ix = new EmailIndex(DbFile);
        ix.UpsertBatch(Enumerable.Range(0, 20).Select(i => Mail(i)).ToList());
        Assert.Equal(1, ix.PurgeOlderThan(Base.AddDays(1)));
        Assert.Equal(19, ix.Status().TotalEmails);
        Assert.Single(ix.Search(new SearchFilter { Query = "word19" }));
    }

    [Fact]
    public void Purge_NothingOldOrEmpty_ReturnsZero()
    {
        var ix = new EmailIndex(DbFile);
        Assert.Equal(0, ix.PurgeOlderThan(Base));
        ix.UpsertBatch([Mail(3)]);
        Assert.Equal(0, ix.PurgeOlderThan(Base));
        Assert.Equal(0, ix.PurgeOlderThan(Base.AddDays(3))); // event_time == cutoff stays
    }

    [Fact]
    public void Status_EmptyIndex()
    {
        var ix = new EmailIndex(DbFile);
        var s = ix.Status();
        Assert.Equal(0, s.TotalEmails);
        Assert.Empty(s.FolderCounts);
        Assert.Null(s.Oldest);
        Assert.Null(s.Newest);
        Assert.Null(s.LastSync);
        Assert.False(s.BackfillComplete);
        Assert.True(s.DbSizeBytes > 0);
    }

    [Fact]
    public void Status_CountsPerFolderAndRange()
    {
        var ix = new EmailIndex(DbFile);
        ix.UpsertBatch([Mail(1), Mail(2), Mail(9, "Sent Items")]);
        var s = ix.Status();
        Assert.Equal(3, s.TotalEmails);
        Assert.Equal(2, s.FolderCounts["Inbox"]);
        Assert.Equal(1, s.FolderCounts["Sent Items"]);
        Assert.Equal(Base.AddDays(1), s.Oldest);
        Assert.Equal(Base.AddDays(9), s.Newest);
    }

    [Fact]
    public void SyncState_RoundTripAndUpsert()
    {
        var ix = new EmailIndex(DbFile);
        Assert.Null(ix.GetSyncState("k1"));
        var st = new SyncState("k1", "Inbox", Base, Base.AddDays(-3), Base.AddDays(1), false);
        ix.SetSyncState(st);
        Assert.Equal(st, ix.GetSyncState("k1"));
        var st2 = st with { BackfillComplete = true, ModifiedWatermark = Base.AddDays(2) };
        ix.SetSyncState(st2);
        Assert.Equal(st2, ix.GetSyncState("k1"));
        ix.SetSyncState(new SyncState("k2"));
        Assert.Equal(new SyncState("k2"), ix.GetSyncState("k2"));
    }

    [Fact]
    public void Status_LastSyncAndBackfillComplete()
    {
        var ix = new EmailIndex(DbFile);
        ix.SetSyncState(new SyncState("a", "Inbox", LastReconcile: Base.AddDays(1), BackfillComplete: true));
        Assert.True(ix.Status().BackfillComplete);
        ix.SetSyncState(new SyncState("b", "Sent Items", LastReconcile: Base.AddDays(4), BackfillComplete: false));
        var s = ix.Status();
        Assert.False(s.BackfillComplete);
        Assert.Equal(Base.AddDays(4), s.LastSync);
    }

    [Fact]
    public void Close_AllowsDeletingFiles_AndIsIdempotent()
    {
        var ix = new EmailIndex(DbFile);
        ix.UpsertBatch([Mail(1)]);
        ix.Close();
        ix.Close();
        File.Delete(DbFile);
        foreach (var ext in new[] { "-wal", "-shm" })
            if (File.Exists(DbFile + ext)) File.Delete(DbFile + ext);
        Assert.False(File.Exists(DbFile));
    }
}
