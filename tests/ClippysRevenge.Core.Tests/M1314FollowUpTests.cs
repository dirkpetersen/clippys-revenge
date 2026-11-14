using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Final M13/M14 review follow-ups: indexed body cap, batch isolation, rune-safe heading, ReadText back-off.</summary>
public sealed class M1314FollowUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-fu-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _index;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public M1314FollowUpTests() => _index = new EmailIndex(Path.Combine(_dir, "index.db"));

    public void Dispose()
    {
        _index.Close();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IndexedEmail Mail(string id, string body) => new()
    {
        EntryId = id, StoreId = "s", InternetMessageId = "<" + id + "@x>", Folder = "Inbox", Subject = "S " + id,
        SenderName = "A", SenderEmail = "a@example.com", To = "", Cc = "", Body = body, Unread = false,
        EventTime = T0, LastModified = T0, Attachments = [],
    };

    [Fact]
    public void Body_Over_The_Cap_Is_Cut_And_Marked()
    {
        _index.UpsertBatch([Mail("big", new string('a', EmailIndex.MaxIndexedBodyChars + 1))]);
        var body = _index.Get("big")!.Body!;
        Assert.Equal(EmailIndex.MaxIndexedBodyChars + EmailIndex.BodyCutMarker.Length, body.Length);
        Assert.EndsWith(EmailIndex.BodyCutMarker, body);
    }

    [Fact]
    public void Body_At_The_Cap_Is_Stored_Whole()
    {
        var text = new string('a', EmailIndex.MaxIndexedBodyChars);
        _index.UpsertBatch([Mail("exact", text)]);
        Assert.Equal(text, _index.Get("exact")!.Body);
    }

    [Fact]
    public void Cut_Never_Leaves_A_Dangling_Surrogate()
    {
        var text = new string('a', EmailIndex.MaxIndexedBodyChars - 1) + "\U0001F600" + "tail";
        _index.UpsertBatch([Mail("sur", text)]);
        var body = _index.Get("sur")!.Body!;
        Assert.Equal(EmailIndex.MaxIndexedBodyChars - 1 + EmailIndex.BodyCutMarker.Length, body.Length);
        Assert.Equal('a', body[EmailIndex.MaxIndexedBodyChars - 2]);
        Assert.StartsWith("\n\n[", body[(EmailIndex.MaxIndexedBodyChars - 1)..]);
    }

    [Fact]
    public void Failing_Item_Is_Skipped_And_The_Rest_Of_The_Batch_Is_Stored()
    {
        _index.BeforeUpsertItem = e =>
        {
            if (e.EntryId == "bad") throw new SqliteException("out of memory", 7);
        };
        _index.UpsertBatch([Mail("a", "one"), Mail("bad", "two"), Mail("c", "three")]);
        Assert.NotNull(_index.Get("a"));
        Assert.Null(_index.Get("bad"));
        Assert.NotNull(_index.Get("c"));
    }

    [Fact]
    public void Other_Sqlite_Errors_Still_Throw()
    {
        _index.BeforeUpsertItem = _ => throw new SqliteException("corrupt", 11);
        Assert.Throws<SqliteException>(() => _index.UpsertBatch([Mail("a", "one")]));
    }

    [Fact]
    public void Heading_Drops_Tag_Characters_And_Lone_Surrogates()
    {
        Assert.Equal("ab:", MeetingOffers.Heading("a\U000E0041b"));
        Assert.Equal("ab:", MeetingOffers.Heading("a\uD800b"));
        Assert.Equal("ab:", MeetingOffers.Heading("a\uDC00b"));
        Assert.Equal("a\U0001F600b:", MeetingOffers.Heading("a\U0001F600b"));
    }

    [Fact]
    public void ReadText_Backs_Off_At_Most_Three_Continuation_Bytes()
    {
        Batteries_V2.Init();
        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_open(":memory:", out var db));
        try
        {
            Assert.Equal(raw.SQLITE_OK, raw.sqlite3_prepare_v2(db, "SELECT CAST(?1 AS TEXT)", out var stmt));
            try
            {
                var bytes = new byte[100 + 7_000];
                Array.Fill(bytes, (byte)'a', 0, 100);
                Array.Fill(bytes, (byte)0x80, 100, 7_000);
                Assert.Equal(raw.SQLITE_OK, raw.sqlite3_bind_blob(stmt, 1, bytes));
                Assert.Equal(raw.SQLITE_ROW, raw.sqlite3_step(stmt));
                var cut = 0;
                var s = IndexQuery.ReadText(stmt, 0, ref cut);
                Assert.Equal(1, cut);
                Assert.Equal(IndexQuery.MaxCellChars + 1, s.Length);   // not backed off down to the 100 leading bytes
                Assert.EndsWith("…", s);
            }
            finally { stmt.Dispose(); }
        }
        finally { db.Dispose(); }
    }
}
