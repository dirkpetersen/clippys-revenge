using ClippysRevenge.Core;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexThreadAttachmentsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-ta-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private string DbFile => Path.Combine(_dir, "index.db");

    public EmailIndexThreadAttachmentsTests() => _ix = new EmailIndex(DbFile);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DateTimeOffset T(int minutes) => Now.AddMinutes(minutes);

    private static IndexedEmail Mail(string id, int minutes, string? topic = "Budget", string folder = "Inbox",
        string? body = null, AttachmentInfo[]? atts = null) => new()
    {
        EntryId = id,
        Folder = folder,
        Subject = "s",
        SenderName = "Sender " + id,
        SenderEmail = id.ToLowerInvariant() + "@contoso.com",
        Body = body,
        ConversationTopic = topic,
        EventTime = T(minutes),
        Attachments = atts ?? [],
    };

    [Fact]
    public void Returns_the_thread_newest_first_without_the_anchor()
    {
        _ix.UpsertBatch(
        [
            Mail("T1", -40, atts: [new AttachmentInfo(1, "a.pdf", 10, false)]),
            Mail("T2", -30, folder: "Sent Items", atts: [new AttachmentInfo(1, "b.pdf", 20, false), new AttachmentInfo(2, "logo.png", 5, true)]),
            Mail("T3", -20),
            Mail("T4", -10, folder: "Archive"),
            Mail("A", 0),
        ]);

        var r = _ix.ThreadAttachments("budget", T(0), "A");
        Assert.Equal(["T4", "T3", "T2", "T1"], r.Select(x => x.EntryId).ToArray());
        Assert.Empty(r[0].Attachments);
        Assert.Empty(r[1].Attachments);
        Assert.Equal(["b.pdf", "logo.png"], r[2].Attachments.Select(a => a.FileName).ToArray());
        Assert.True(r[2].Attachments[1].IsInline);
        Assert.False(r[2].Attachments[0].IsInline);
        Assert.Equal("t4@contoso.com", r[0].SenderEmail);
        Assert.Equal(T(-10).ToUnixTimeSeconds(), r[0].EventTime.ToUnixTimeSeconds());
    }

    [Fact]
    public void Deleted_items_and_its_subfolders_are_never_returned()
    {
        _ix.UpsertBatch(
        [
            Mail("T1", -40, folder: "Deleted Items"),
            Mail("T2", -30, folder: "deleted items\\Old"),
            Mail("T3", -20, folder: "Drafts"),
            Mail("T4", -10, folder: "Deleted Items Archive"),
            Mail("A", 0),
        ]);

        var r = _ix.ThreadAttachments("budget", T(0), "A");
        Assert.Equal(["T4", "T3"], r.Select(x => x.EntryId).ToArray());
    }

    [Fact]
    public void UpTo_is_inclusive()
    {
        _ix.UpsertBatch([Mail("T1", -5), Mail("T2", 0), Mail("T3", 5)]);
        var r = _ix.ThreadAttachments("Budget", T(0), "none");
        Assert.Equal(["T2", "T1"], r.Select(x => x.EntryId).ToArray());
    }

    [Fact]
    public void MaxMessages_and_argument_checks()
    {
        _ix.UpsertBatch([Mail("T1", -3), Mail("T2", -2), Mail("T3", -1)]);
        Assert.Equal(["T3", "T2"], _ix.ThreadAttachments("Budget", T(0), "x", 2).Select(m => m.EntryId).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.ThreadAttachments("Budget", T(0), "x", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.ThreadAttachments("Budget", T(0), "x", 101));
        Assert.Throws<ArgumentNullException>(() => _ix.ThreadAttachments("Budget", T(0), null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Other topic")]
    public void Blank_or_other_topic_gives_nothing(string? topic)
    {
        _ix.UpsertBatch([Mail("T1", -3)]);
        Assert.Empty(_ix.ThreadAttachments(topic, T(0), "x"));
    }

    [Fact]
    public void Unknown_size_stays_null_and_positions_are_ordered()
    {
        _ix.UpsertBatch([Mail("T1", -3, atts: [new AttachmentInfo(2, "b.pdf", 7, false), new AttachmentInfo(1, "a.pdf", null, false)])]);
        var a = _ix.ThreadAttachments("Budget", T(0), "x").Single().Attachments;
        Assert.Equal([1, 2], a.Select(x => x.Position).ToArray());
        Assert.Null(a[0].Size);
        Assert.Equal(7, a[1].Size);
    }

    [Fact]
    public void Body_is_cut_to_the_thread_limit()
    {
        _ix.UpsertBatch([Mail("T1", -3, body: new string('x', EmailIndex.MaxThreadBodyChars + 500))]);
        var m = _ix.ThreadAttachments("Budget", T(0), "x").Single();
        Assert.Equal(EmailIndex.MaxThreadBodyChars, m.Body!.Length);
    }

    [Fact]
    public void Query_writes_nothing()
    {
        _ix.UpsertBatch([Mail("T1", -3, atts: [new AttachmentInfo(1, "a.pdf", 1, false)])]);
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = false }.ToString());
        raw.Open();
        var before = raw.ExecuteScalar<long>("PRAGMA data_version");
        _ = _ix.ThreadAttachments("Budget", T(0), "x");
        Assert.Equal(before, raw.ExecuteScalar<long>("PRAGMA data_version"));
    }
}
