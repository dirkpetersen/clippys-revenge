using ClippysRevenge.Core;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-mig-" + Guid.NewGuid().ToString("N"));
    private string V1Path => Path.Combine(_dir, "v1.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string NewV1()
    {
        SchemaV1.Create(V1Path, SchemaV1.Sample());
        return V1Path;
    }

    private static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static string Snapshot(string path)
    {
        using var c = Raw(path);
        var rows = c.Query<(long id, string entry_id, string folder, string subject, string body, long event_time,
            long last_modified, long unread, long has_attachments, string? attachment_names)>(
            "SELECT id, entry_id, folder, subject, body, event_time, last_modified, unread, has_attachments, attachment_names FROM emails ORDER BY id");
        var atts = c.Query<(long email_id, long position, string filename)>(
            "SELECT email_id, position, filename FROM attachments ORDER BY email_id, position");
        return string.Join("|", rows) + "##" + string.Join("|", atts);
    }

    private static long Count(string path, string sql, object? p = null)
    {
        using var c = Raw(path);
        return c.ExecuteScalar<long>(sql, p);
    }

    private static string? Meta(string path, string key)
    {
        using var c = Raw(path);
        return c.ExecuteScalar<string?>("SELECT value FROM meta WHERE key = @key", new { key });
    }

    private static void AssertV2Objects(string path)
    {
        using var c = Raw(path);
        var names = c.Query<(string type, string name)>("SELECT type, name FROM sqlite_master").ToList();
        Assert.Contains(("table", "ai_decisions"), names);
        foreach (var i in new[] { "ix_emails_irt", "ix_emails_topic", "ix_ai_decided", "ix_ai_imid" })
            Assert.Contains(("index", i), names);
        foreach (var col in new[] { "in_reply_to", "conversation_topic", "clippy_marker" })
            Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info('emails') WHERE name = @col", new { col }));
    }

    private static void AssertV3Objects(string path)
    {
        using var c = Raw(path);
        var names = c.Query<(string type, string name)>("SELECT type, name FROM sqlite_master").ToList();
        foreach (var t in new[] { "ai_drafts", "ai_revisits" })
            Assert.Contains(("table", t), names);
        foreach (var i in new[] { "ix_ai_drafts_state", "ix_ai_revisits_decided" })
            Assert.Contains(("index", i), names);
    }

    [Fact]
    public void V1_file_migrates_to_version_5()
    {
        var path = NewV1();
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV2Objects(path);
        AssertV3Objects(path);
    }

    [Fact]
    public void Rows_are_kept_and_new_columns_are_null()
    {
        var path = NewV1();
        var before = Snapshot(path);
        _ = new EmailIndex(path);
        Assert.Equal(before, Snapshot(path));
        Assert.Equal(4, Count(path, "SELECT COUNT(*) FROM emails WHERE in_reply_to IS NULL AND conversation_topic IS NULL AND clippy_marker IS NULL"));
    }

    [Fact]
    public void Fts_is_kept()
    {
        var path = NewV1();
        long[] Fts()
        {
            using var c = Raw(path);
            return c.Query<long>("SELECT rowid FROM emails_fts WHERE emails_fts MATCH @q ORDER BY rowid", new { q = FtsQuery.Build("budget") }).ToArray();
        }
        var before = Fts();
        Assert.NotEmpty(before);
        var ix = new EmailIndex(path);
        using (var c = Raw(path)) c.Execute("INSERT INTO emails_fts(emails_fts, rank) VALUES('integrity-check', 1)");
        Assert.Equal(before, Fts());
        var hits = ix.Search(new SearchFilter { Query = "budget" }).Select(h => h.Id).OrderBy(x => x).ToArray();
        Assert.Equal(["A1", "S1"], hits);
    }

    [Fact]
    public void Meta_and_sync_state_unchanged_and_refresh_key_unset()
    {
        var path = NewV1();
        var ix = new EmailIndex(path);
        Assert.Equal("1763000000", Meta(path, "last_sync"));
        var s = Assert.Single(ix.ListSyncStates());
        Assert.Equal("fk-inbox", s.FolderKey);
        Assert.True(s.BackfillComplete);
        Assert.Null(ix.GetMeta("thread_meta_complete"));
    }

    [Fact]
    public void Reconcile_view_unchanged()
    {
        var path = NewV1();
        var ix = new EmailIndex(path);
        var entries = ix.ListEntries("Inbox");
        Assert.Equal(["A2", "A1"], entries.Select(e => e.EntryId));
        Assert.Equal([false, true], entries.Select(e => e.Unread));
        var info = ix.GetSyncInfo(["A1", "A2"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_763_000_100), info["A1"].LastModified);
        Assert.True(info["A1"].Unread);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_763_000_600), info["A2"].LastModified);
        Assert.False(info["A2"].Unread);
    }

    [Fact]
    public void Partly_migrated_file_is_finished_idempotently()
    {
        var path = NewV1();
        using (var c = Raw(path))
        {
            c.Execute("ALTER TABLE emails ADD COLUMN in_reply_to TEXT");
            c.Execute("CREATE INDEX ix_emails_irt ON emails(in_reply_to)");
        }
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV2Objects(path);
        AssertV3Objects(path);
    }

    [Fact]
    public void Reopening_changes_nothing()
    {
        var path = NewV1();
        _ = new EmailIndex(path);
        var before = Snapshot(path);
        for (var i = 0; i < 3; i++) _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        Assert.Equal(before, Snapshot(path));
        Assert.Null(Meta(path, "thread_meta_complete"));
        AssertV2Objects(path);
        AssertV3Objects(path);
    }

    [Fact]
    public void Failure_in_step_2_rolls_everything_back()
    {
        var path = NewV1();
        var before = Snapshot(path);
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 2) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("1", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM pragma_table_info('emails') WHERE name = 'in_reply_to'"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ai_decisions'"));
        Assert.Equal(before, Snapshot(path));

        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV2Objects(path);
        AssertV3Objects(path);
        Assert.Equal(before, Snapshot(path));
    }

    [Fact]
    public async Task Concurrent_constructors_all_succeed()
    {
        var path = NewV1();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new EmailIndex(path))));
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV2Objects(path);
        AssertV3Objects(path);
    }

    [Fact]
    public void New_file_is_created_at_version_2_and_needs_no_refresh()
    {
        var path = Path.Combine(_dir, "new.db");
        var ix = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV2Objects(path);
        Assert.Equal("1", ix.GetMeta("thread_meta_complete"));
    }

    [Fact]
    public void Newer_schema_is_refused_and_file_unchanged()
    {
        var path = NewV1();
        using (var c = Raw(path)) c.Execute("UPDATE meta SET value = '6' WHERE key = 'schema_version'");
        var before = Snapshot(path);
        var ex = Assert.Throws<InvalidOperationException>(() => new EmailIndex(path));
        Assert.Contains("supports up to 5", ex.Message);
        Assert.Equal("6", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM pragma_table_info('emails') WHERE name = 'in_reply_to'"));
        Assert.Equal(before, Snapshot(path));
    }

    [Fact]
    public void Purge_after_migration_keeps_fts_intact()
    {
        var path = NewV1();
        var ix = new EmailIndex(path);
        var removed = ix.PurgeOlderThan(DateTimeOffset.FromUnixTimeSeconds(1_763_000_800));
        Assert.Equal(2, removed);
        Assert.Equal(["S1", "S2"], ix.ListEntries("Sent Items").Select(e => e.EntryId).OrderBy(x => x));
        Assert.Empty(ix.ListEntries("Inbox"));
        using var c = Raw(path);
        c.Execute("INSERT INTO emails_fts(emails_fts, rank) VALUES('integrity-check', 1)");
    }

    private string V2Path => Path.Combine(_dir, "v2.db");

    private string NewV2()
    {
        SchemaV2.Create(V2Path);
        return V2Path;
    }

    private static string FullSnapshot(string path)
    {
        using var c = Raw(path);
        var dec = c.Query<(string, string?, string?, long, string, string?, long, long, double?, string?, long?)>(
            "SELECT * FROM ai_decisions ORDER BY source_entry_id");
        var meta = c.Query<(string, string?)>("SELECT key, value FROM meta WHERE key <> 'schema_version' ORDER BY key");
        var topics = c.Query<(string, string?)>("SELECT entry_id, conversation_topic FROM emails ORDER BY entry_id");
        return Snapshot(path) + "##" + string.Join("|", dec) + "##" + string.Join("|", meta) + "##" + string.Join("|", topics);
    }

    [Fact]
    public void V2_file_migrates_to_version_5_and_keeps_rows()
    {
        var path = NewV2();
        var before = FullSnapshot(path);
        var ix = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        Assert.Equal(before, FullSnapshot(path));
        Assert.Equal("1", Meta(path, "thread_meta_complete"));
        AssertV3Objects(path);
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM ai_drafts"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM ai_revisits"));
        var d = ix.GetDecision("A1")!;
        Assert.Equal(AiOutcome.Drafted, d.Outcome);
        Assert.Equal("D1", d.DraftEntryId);
    }

    [Fact]
    public void Failure_in_step_3_rolls_back_to_version_2()
    {
        var path = NewV2();
        var before = FullSnapshot(path);
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 3) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("2", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ai_drafts'"));
        Assert.Equal(before, FullSnapshot(path));
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV3Objects(path);
    }

    [Fact]
    public void V1_file_goes_to_3_in_one_transaction()
    {
        var path = NewV1();
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 3) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("1", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ai_decisions', 'ai_drafts', 'ai_revisits')"));
    }

    [Fact]
    public void Reopening_v3_changes_nothing()
    {
        var path = NewV2();
        var ix = new EmailIndex(path);
        var fp = new string('a', 64);
        ix.RecordAiDraft("D", "A1", DateTimeOffset.FromUnixTimeSeconds(1_763_000_700), fp,
            DateTimeOffset.FromUnixTimeSeconds(1_763_000_000), DateTimeOffset.FromUnixTimeSeconds(1_763_000_800));
        var before = FullSnapshot(path);
        for (var i = 0; i < 3; i++) _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        Assert.Equal(before, FullSnapshot(path));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM ai_drafts"));
    }

    [Fact]
    public void Partly_created_v3_tables_are_finished()
    {
        var path = NewV2();
        using (var c = Raw(path))
            c.Execute("""
                CREATE TABLE ai_drafts (
                  draft_entry_id TEXT PRIMARY KEY,
                  source_entry_id TEXT NOT NULL,
                  created INTEGER NOT NULL,
                  fingerprint TEXT NOT NULL,
                  checked INTEGER NOT NULL,
                  state TEXT NOT NULL,
                  attempts INTEGER NOT NULL DEFAULT 0,
                  updated INTEGER NOT NULL)
                """);
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV3Objects(path);
    }

    // ---- schema 4 (ai_calls) ----

    private string V3Path => Path.Combine(_dir, "v3.db");

    private string NewV3()
    {
        SchemaV3.Create(V3Path);
        return V3Path;
    }

    private static void AssertV4Objects(string path)
    {
        using var c = Raw(path);
        var names = c.Query<(string type, string name)>("SELECT type, name FROM sqlite_master").ToList();
        Assert.Contains(("table", "ai_calls"), names);
        Assert.Contains(("index", "ix_ai_calls_at"), names);
    }

    private static string V3Snapshot(string path)
    {
        using var c = Raw(path);
        var drafts = c.Query<(string, string, long, string, long, string, long, long)>("SELECT * FROM ai_drafts ORDER BY draft_entry_id");
        var revisits = c.Query<(long, string, long, string, long)>("SELECT * FROM ai_revisits ORDER BY id");
        return FullSnapshot(path) + "##" + string.Join("|", drafts) + "##" + string.Join("|", revisits);
    }

    [Fact]
    public void V3_file_migrates_to_version_5_and_keeps_rows()
    {
        var path = NewV3();
        var before = V3Snapshot(path);
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV4Objects(path);
        Assert.Equal(before, V3Snapshot(path));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM ai_calls"));
    }

    [Fact]
    public void Failure_in_step_4_rolls_back_to_version_3()
    {
        var path = NewV3();
        var before = V3Snapshot(path);
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 4) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("3", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ai_calls'"));
        Assert.Equal(before, V3Snapshot(path));
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV4Objects(path);
    }

    [Fact]
    public void V1_file_goes_to_4_in_one_transaction()
    {
        var path = NewV1();
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 4) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("1", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ai_decisions', 'ai_drafts', 'ai_revisits', 'ai_calls')"));
    }

    [Fact]
    public void Partly_created_v4_table_is_finished()
    {
        var path = NewV3();
        using (var c = Raw(path))
            c.Execute("""
                CREATE TABLE ai_calls (
                  id INTEGER PRIMARY KEY,
                  at INTEGER NOT NULL,
                  kind TEXT NOT NULL,
                  backend TEXT NOT NULL,
                  outcome TEXT NOT NULL,
                  input INTEGER, output INTEGER, cache_read INTEGER, cache_creation INTEGER)
                """);
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV4Objects(path);
    }

    [Fact]
    public void Reopening_v4_changes_nothing()
    {
        var path = NewV3();
        var ix = new EmailIndex(path);
        ix.AddAiCall(DateTimeOffset.FromUnixTimeSeconds(1_763_000_000), ModelCallKind.Draft, "cli", true, new TokenUsage(1, 2, null, null));
        var before = V3Snapshot(path);
        for (var i = 0; i < 3; i++) _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        Assert.Equal(before, V3Snapshot(path));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM ai_calls"));
    }

    // ---- schema 5 (ai_draft_texts, ai_edits) ----

    private string V4Path => Path.Combine(_dir, "v4.db");

    private string NewV4()
    {
        SchemaV4.Create(V4Path);
        return V4Path;
    }

    private static void AssertV5Objects(string path)
    {
        using var c = Raw(path);
        var names = c.Query<(string type, string name)>("SELECT type, name FROM sqlite_master").ToList();
        foreach (var t in new[] { "ai_draft_texts", "ai_edits" })
            Assert.Contains(("table", t), names);
        foreach (var i in new[] { "ix_ai_draft_texts_imid", "ix_ai_edits_sent" })
            Assert.Contains(("index", i), names);
    }

    private static string V4Snapshot(string path)
    {
        using var c = Raw(path);
        var calls = c.Query<(long, long, string, string, string, long?, long?, long?, long?)>("SELECT * FROM ai_calls ORDER BY id");
        return V3Snapshot(path) + "##" + string.Join("|", calls);
    }

    [Fact]
    public void V4_file_migrates_to_version_5_and_keeps_rows()
    {
        var path = NewV4();
        var before = V4Snapshot(path);
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV5Objects(path);
        Assert.Equal(before, V4Snapshot(path));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM ai_calls"));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM ai_drafts"));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM ai_revisits"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM ai_draft_texts"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM ai_edits"));
    }

    [Fact]
    public void Failure_in_step_5_rolls_back_to_version_4()
    {
        var path = NewV4();
        var before = V4Snapshot(path);
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 5) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("4", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ai_draft_texts', 'ai_edits')"));
        Assert.Equal(before, V4Snapshot(path));
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV5Objects(path);
        Assert.Equal(before, V4Snapshot(path));
    }

    [Fact]
    public void V1_file_goes_to_5_in_one_transaction()
    {
        var path = NewV1();
        Assert.Throws<InvalidOperationException>(() =>
            new EmailIndex(path, n => { if (n == 5) throw new InvalidOperationException("boom"); }));
        SqliteConnection.ClearAllPools();
        Assert.Equal("1", Meta(path, "schema_version"));
        Assert.Equal(0, Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ai_decisions', 'ai_drafts', 'ai_revisits', 'ai_calls', 'ai_draft_texts', 'ai_edits')"));
    }

    [Fact]
    public void Partly_created_v5_table_is_finished()
    {
        var path = NewV4();
        using (var c = Raw(path))
            c.Execute("""
                CREATE TABLE ai_draft_texts (
                  draft_entry_id TEXT PRIMARY KEY,
                  mailbox TEXT NOT NULL,
                  source_imid TEXT NOT NULL,
                  created INTEGER NOT NULL,
                  text TEXT NOT NULL,
                  state TEXT NOT NULL,
                  updated INTEGER NOT NULL)
                """);
        _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        AssertV5Objects(path);
    }

    [Fact]
    public void Reopening_v5_changes_nothing()
    {
        var path = NewV4();
        var ix = new EmailIndex(path);
        ix.RecordAiDraftText("D9", "", "<A@x>", DateTimeOffset.FromUnixTimeSeconds(1_763_000_000), "hello");
        var before = V4Snapshot(path);
        for (var i = 0; i < 3; i++) _ = new EmailIndex(path);
        Assert.Equal("5", Meta(path, "schema_version"));
        Assert.Equal(before, V4Snapshot(path));
        Assert.Equal(1, Count(path, "SELECT COUNT(*) FROM ai_draft_texts"));
    }
}
