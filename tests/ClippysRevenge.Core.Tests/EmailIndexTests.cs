using ClippysRevenge.Core;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-ix-" + Guid.NewGuid().ToString("N"));
    private string DbFile => Path.Combine(_dir, "index.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private EmailIndex NewIndex() => new(DbFile);

    private static IndexedEmail Mail(string entryId, string subject = "subject", string folder = "Inbox",
        string? body = null, bool unread = false, DateTimeOffset? lastModified = null,
        IReadOnlyList<AttachmentInfo>? attachments = null) => new()
    {
        EntryId = entryId,
        Folder = folder,
        Subject = subject,
        Body = body,
        Unread = unread,
        LastModified = lastModified,
        EventTime = DateTimeOffset.Parse("2026-03-01T08:15:30Z"),
        Attachments = attachments ?? [],
    };

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = true }.ToString());
        c.Open();
        return c;
    }

    private T Scalar<T>(string sql, object? p = null)
    {
        using var c = Raw();
        return c.ExecuteScalar<T>(sql, p)!;
    }

    private long[] Match(string text)
    {
        using var c = Raw();
        return c.Query<long>("SELECT rowid FROM emails_fts WHERE emails_fts MATCH @q ORDER BY rowid",
            new { q = FtsQuery.Build(text) }).ToArray();
    }

    private void AssertFtsIntegrity()
    {
        using var c = Raw();
        c.Execute("INSERT INTO emails_fts(emails_fts, rank) VALUES('integrity-check', 1)");
    }

    // ---- Schema / migration ----

    [Fact]
    public void Ctor_NewFile_CreatesSchema()
    {
        NewIndex();
        using var c = Raw();
        var names = c.Query<(string type, string name)>("SELECT type, name FROM sqlite_master").ToList();
        foreach (var t in new[] { "emails", "emails_fts", "attachments", "sync_state", "meta", "ai_decisions" })
            Assert.Contains(("table", t), names);
        foreach (var i in new[] { "ix_emails_time", "ix_emails_folder_time", "ix_emails_imid", "ix_emails_sender", "ix_att_name",
                     "ix_emails_irt", "ix_emails_topic", "ix_ai_decided", "ix_ai_imid" })
            Assert.Contains(("index", i), names);
        foreach (var t in new[] { "emails_ai", "emails_ad", "emails_au" })
            Assert.Contains(("trigger", t), names);
    }

    [Fact]
    public void Ctor_NewFile_WritesSchemaVersion2()
    {
        NewIndex();
        Assert.Equal("2", Scalar<string>("SELECT value FROM meta WHERE key='schema_version'"));
        Assert.Equal(2, EmailIndex.SchemaVersion);
    }

    [Fact]
    public void Ctor_CreatesMissingParentDirectory()
    {
        var path = Path.Combine(_dir, "a", "b", "index.db");
        _ = new EmailIndex(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Ctor_Reopen_KeepsData()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A"), Mail("B")]);
        var a = ix.Get("A")!;
        var b = ix.Get("B")!;
        var ix2 = NewIndex();
        Assert.Equal(a.RowId, ix2.Get("A")!.RowId);
        Assert.Equal(b.RowId, ix2.Get("B")!.RowId);
        Assert.Equal("2", Scalar<string>("SELECT value FROM meta WHERE key='schema_version'"));
    }

    [Fact]
    public async Task Ctor_TwoInstancesConcurrently_NoError()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new EmailIndex(DbFile))));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM meta WHERE key='schema_version'"));
    }

    [Fact]
    public void Ctor_NewerSchemaVersion_Throws()
    {
        NewIndex();
        using (var c = Raw()) c.Execute("UPDATE meta SET value='99' WHERE key='schema_version'");
        SqliteConnection.ClearAllPools();
        var ex = Assert.Throws<InvalidOperationException>(() => new EmailIndex(DbFile));
        Assert.Contains("schema version 99", ex.Message);
        Assert.Contains("supports up to 2", ex.Message);
    }

    [Fact]
    public void Ctor_NonIntegerSchemaVersion_Throws()
    {
        NewIndex();
        using (var c = Raw()) c.Execute("UPDATE meta SET value='abc' WHERE key='schema_version'");
        SqliteConnection.ClearAllPools();
        var ex = Assert.Throws<InvalidOperationException>(() => new EmailIndex(DbFile));
        Assert.Contains("schema version abc", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Ctor_InvalidPath_Throws(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new EmailIndex(path!));
    }

    [Fact]
    public void Schema_HasNoAttachmentContentColumn()
    {
        NewIndex();
        using var c = Raw();
        Assert.Equal(
            ["id", "entry_id", "store_id", "internet_message_id", "folder", "subject", "sender_name", "sender_email",
             "to_addrs", "cc_addrs", "event_time", "last_modified", "unread", "has_attachments", "attachment_names", "body",
             "in_reply_to", "conversation_topic", "clippy_marker"],
            c.Query<string>("SELECT name FROM pragma_table_info('emails') ORDER BY cid").ToArray());
        Assert.Equal(
            ["email_id", "position", "filename", "size", "is_inline"],
            c.Query<string>("SELECT name FROM pragma_table_info('attachments') ORDER BY cid").ToArray());
    }

    [Fact]
    public void OpenConnection_AppliesPragmas()
    {
        var ix = NewIndex();
        for (var i = 0; i < 2; i++)
        {
            using var c = ix.OpenConnection();
            Assert.Equal("wal", c.ExecuteScalar<string>("PRAGMA journal_mode"));
            Assert.Equal(1L, c.ExecuteScalar<long>("PRAGMA synchronous"));
            Assert.Equal(1L, c.ExecuteScalar<long>("PRAGMA foreign_keys"));
            Assert.Equal(5000L, c.ExecuteScalar<long>("PRAGMA busy_timeout"));
        }
    }

    // ---- UpsertBatch / Get ----

    [Fact]
    public void Upsert_Insert_GetRoundTripsAllFields()
    {
        var ix = NewIndex();
        ix.UpsertBatch([new IndexedEmail
        {
            EntryId = "E1",
            StoreId = "S1",
            InternetMessageId = "<m1@x>",
            Folder = "Inbox",
            Subject = "Budget 2026",
            SenderName = "Doe, Jane",
            SenderEmail = "jane@x.org",
            To = "A <a@x.org>; B <b@x.org>",
            Cc = "c@x.org",
            EventTime = DateTimeOffset.Parse("2026-03-01T10:15:30.789+02:00"),
            LastModified = DateTimeOffset.Parse("2026-03-02T00:00:00Z"),
            Unread = true,
            Body = "hello",
            Attachments = [new(1, "a.pdf", 100, false), new(2, "image001.png", 5, true)],
        }]);
        var d = ix.Get("E1")!;
        Assert.True(d.RowId > 0);
        Assert.Equal("S1", d.StoreId);
        Assert.Equal("<m1@x>", d.InternetMessageId);
        Assert.Equal("Inbox", d.Folder);
        Assert.Equal("Budget 2026", d.Subject);
        Assert.Equal("Doe, Jane", d.SenderName);
        Assert.Equal("jane@x.org", d.SenderEmail);
        Assert.Equal("A <a@x.org>; B <b@x.org>", d.To);
        Assert.Equal("c@x.org", d.Cc);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T08:15:30Z"), d.EventTime);
        Assert.Equal(TimeSpan.Zero, d.EventTime.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T00:00:00Z"), d.LastModified);
        Assert.True(d.Unread);
        Assert.True(d.HasAttachments);
        Assert.Equal("hello", d.Body);
        Assert.Equal([new AttachmentInfo(1, "a.pdf", 100, false), new AttachmentInfo(2, "image001.png", 5, true)], d.Attachments);
        Assert.Equal(1772352930L, Scalar<long>("SELECT event_time FROM emails WHERE entry_id='E1'"));
        Assert.Equal("a.pdf\nimage001.png", Scalar<string>("SELECT attachment_names FROM emails WHERE entry_id='E1'"));
    }

    [Fact]
    public void Upsert_NullOptionalFields_RoundTripAsNull()
    {
        var ix = NewIndex();
        ix.UpsertBatch([new IndexedEmail { EntryId = "E", Folder = "Inbox", EventTime = DateTimeOffset.UnixEpoch }]);
        var d = ix.Get("E")!;
        Assert.Null(d.StoreId);
        Assert.Null(d.InternetMessageId);
        Assert.Null(d.Subject);
        Assert.Null(d.SenderName);
        Assert.Null(d.SenderEmail);
        Assert.Null(d.To);
        Assert.Null(d.Cc);
        Assert.Null(d.Body);
        Assert.Null(d.LastModified);
        Assert.False(d.Unread);
        Assert.Empty(d.Attachments);
        Assert.False(d.HasAttachments);
        Assert.True(Scalar<bool>("SELECT attachment_names IS NULL FROM emails WHERE entry_id='E'"));
    }

    [Fact]
    public void Upsert_Update_KeepsRowIdAndReplacesFields()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A"), Mail("B")]);
        var a = ix.Get("A")!;
        var rawId = Scalar<long>("SELECT id FROM emails WHERE entry_id='A'");
        ix.UpsertBatch([Mail("A", subject: "new subject", body: "new body", unread: true)]);
        var a2 = ix.Get("A")!;
        Assert.Equal(a.RowId, a2.RowId);
        Assert.Equal("new subject", a2.Subject);
        Assert.Equal("new body", a2.Body);
        Assert.True(a2.Unread);
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM emails"));
        Assert.Equal(rawId, Scalar<long>("SELECT id FROM emails WHERE entry_id='A'"));
    }

    [Fact]
    public void Upsert_Update_ReindexesFts()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", subject: "alpha")]);
        var id = ix.Get("A")!.RowId;
        Assert.Equal([id], Match("alpha"));
        ix.UpsertBatch([Mail("A", subject: "bravo")]);
        Assert.Empty(Match("alpha"));
        Assert.Equal([id], Match("bravo"));
        AssertFtsIntegrity();
    }

    [Fact]
    public void Upsert_Update_ReplacesAttachments()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", attachments: [new(1, "a.pdf", 1, false), new(2, "b.pdf", 1, false), new(3, "c.pdf", 1, false)])]);
        ix.UpsertBatch([Mail("A", attachments: [new(1, "new.docx", 1, false)])]);
        var d = ix.Get("A")!;
        Assert.Equal([new AttachmentInfo(1, "new.docx", 1, false)], d.Attachments);
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM attachments WHERE email_id=@id", new { id = d.RowId }));
        Assert.Equal([d.RowId], Match("new.docx"));
        Assert.Empty(Match("a.pdf"));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("inline", false)]
    [InlineData("both", true)]
    [InlineData("file", true)]
    public void Upsert_HasAttachments_IgnoresInline(string kind, bool expected)
    {
        IReadOnlyList<AttachmentInfo> atts = kind switch
        {
            "none" => [],
            "inline" => [new(1, "i.png", 1, true)],
            "both" => [new(1, "i.png", 1, true), new(2, "f.pdf", 1, false)],
            _ => [new(1, "f.pdf", 1, false)],
        };
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", attachments: atts)]);
        Assert.Equal(expected, ix.Get("A")!.HasAttachments);
    }

    [Fact]
    public void Upsert_InlineNamesAreSearchable()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", attachments: [new(1, "image001.png", 1, true)])]);
        Assert.Equal([ix.Get("A")!.RowId], Match("image001"));
    }

    [Fact]
    public void Upsert_AttachmentsOrderedByPosition()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", attachments: [new(3, "c", 1, false), new(1, "a", 1, false), new(2, "b", 1, false)])]);
        Assert.Equal([1, 2, 3], ix.Get("A")!.Attachments.Select(a => a.Position).ToArray());
    }

    [Fact]
    public void Upsert_DuplicateEntryIdInBatch_LastWins()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", subject: "one"), Mail("A", subject: "two")]);
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM emails"));
        Assert.Equal("two", ix.Get("A")!.Subject);
    }

    [Fact]
    public void Upsert_EmptyBatch_NoOp()
    {
        var ix = NewIndex();
        ix.UpsertBatch([]);
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM emails"));
    }

    [Theory]
    [InlineData("emptyId")]
    [InlineData("blankFolder")]
    [InlineData("dupPos")]
    [InlineData("nullName")]
    public void Upsert_InvalidBatch_WritesNothing(string kind)
    {
        var ix = NewIndex();
        var bad = kind switch
        {
            "emptyId" => Mail(""),
            "blankFolder" => Mail("Y", folder: " "),
            "dupPos" => Mail("Y", attachments: [new(2, "a", 1, false), new(2, "b", 1, false)]),
            _ => Mail("Y", attachments: [new(1, null!, 1, false)]),
        };
        var ex = Assert.Throws<ArgumentException>(() => ix.UpsertBatch([Mail("X"), bad]));
        if (kind == "dupPos") Assert.Contains("Y", ex.Message);
        Assert.Null(ix.Get("X"));
    }

    [Fact]
    public void Upsert_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewIndex().UpsertBatch(null!));
    }

    [Fact]
    public void Upsert_DiacriticsFolded()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", subject: "Café Müller")]);
        var id = ix.Get("A")!.RowId;
        Assert.Equal([id], Match("cafe"));
        Assert.Equal([id], Match("muller"));
    }

    [Fact]
    public void Upsert_QuoteAndApostropheText_Stored()
    {
        var ix = NewIndex();
        const string subj = "O'Example \"quoted\"";
        ix.UpsertBatch([Mail("A", subject: subj)]);
        Assert.Equal(subj, ix.Get("A")!.Subject);
        Assert.Equal([ix.Get("A")!.RowId], Match("O'Example"));
    }

    [Fact]
    public async Task Upsert_ConcurrentBatches_AllWritten()
    {
        var ix = NewIndex();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var b = 0; b < 4; b++)
                ix.UpsertBatch(Enumerable.Range(0, 25).Select(i => Mail($"T{t}-{b * 25 + i}", subject: $"s{t} {i}")).ToList());
        })));
        Assert.Equal(400, Scalar<long>("SELECT COUNT(*) FROM emails"));
        AssertFtsIntegrity();
    }

    [Fact]
    public void Get_Unknown_ReturnsNull() => Assert.Null(NewIndex().Get("nope"));

    [Fact]
    public void Get_IsCaseSensitive()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("ABC")]);
        Assert.Null(ix.Get("abc"));
        Assert.NotNull(ix.Get("ABC"));
    }

    [Fact]
    public void Get_EmptyId_Throws() => Assert.ThrowsAny<ArgumentException>(() => NewIndex().Get(""));

    // ---- Delete ----

    [Fact]
    public void Delete_RemovesRowsAttachmentsAndFts()
    {
        var ix = NewIndex();
        ix.UpsertBatch([
            Mail("A", subject: "aaa", attachments: [new(1, "a.pdf", 1, false)]),
            Mail("B", subject: "bbb", attachments: [new(1, "b.pdf", 1, false)]),
            Mail("C", subject: "ccc", attachments: [new(1, "c.pdf", 1, false)]),
        ]);
        Assert.Equal(2, ix.Delete(["A", "C", "nope"]));
        Assert.Null(ix.Get("A"));
        Assert.Null(ix.Get("C"));
        Assert.NotNull(ix.Get("B"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM attachments"));
        Assert.Empty(Match("aaa"));
        AssertFtsIntegrity();
    }

    [Fact]
    public void Delete_EmptyAndNullIds_ReturnsZero()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A")]);
        Assert.Equal(0, ix.Delete([]));
        Assert.Equal(0, ix.Delete(["", null!]));
        Assert.NotNull(ix.Get("A"));
        Assert.Throws<ArgumentNullException>(() => ix.Delete(null!));
    }

    [Fact]
    public void Delete_ThenReinsert_GetsNewRow()
    {
        var ix = NewIndex();
        ix.UpsertBatch([Mail("A", subject: "zebra")]);
        ix.Delete(["A"]);
        ix.UpsertBatch([Mail("A", subject: "zebra")]);
        Assert.Single(Match("zebra"));
        AssertFtsIntegrity();
    }

    // ---- SetUnread ----

    [Fact]
    public void SetUnread_UpdatesOnlyUnread()
    {
        var ix = NewIndex();
        var lm = DateTimeOffset.Parse("2026-03-02T00:00:00Z");
        ix.UpsertBatch([Mail("A", subject: "alpha", unread: true, lastModified: lm), Mail("B", unread: false)]);
        var rowId = ix.Get("A")!.RowId;
        Assert.Equal(2, ix.SetUnread([("A", false), ("B", true), ("zz", true)]));
        var a = ix.Get("A")!;
        Assert.False(a.Unread);
        Assert.True(ix.Get("B")!.Unread);
        Assert.Equal(lm, a.LastModified);
        Assert.Equal(rowId, a.RowId);
        // 31: FTS stays consistent
        Assert.Equal([rowId], Match("alpha"));
        AssertFtsIntegrity();
    }

    [Fact]
    public void SetUnread_Empty_ReturnsZero()
    {
        var ix = NewIndex();
        Assert.Equal(0, ix.SetUnread([]));
        Assert.Throws<ArgumentNullException>(() => ix.SetUnread(null!));
    }

    // ---- Mixed ----

    [Fact]
    public void MixedOperations_FtsIntegrityHolds()
    {
        var ix = NewIndex();
        ix.UpsertBatch(Enumerable.Range(0, 50).Select(i => Mail($"M{i}", subject: $"subj{i}", body: $"body{i}")).ToList());
        ix.UpsertBatch(Enumerable.Range(0, 20).Select(i => Mail($"M{i}", subject: $"changed{i}", body: $"body{i}")).ToList());
        ix.Delete(Enumerable.Range(40, 10).Select(i => $"M{i}"));
        ix.SetUnread(Enumerable.Range(0, 10).Select(i => ($"M{i}", true)));
        AssertFtsIntegrity();
        Assert.Equal(40, Scalar<long>("SELECT COUNT(*) FROM emails"));
        Assert.Equal(40, Scalar<long>("SELECT COUNT(*) FROM emails_fts"));
    }
}
