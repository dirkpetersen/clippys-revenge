using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

/// <summary>A frozen copy of the schema-1 DDL, so later changes to EmailIndex can't change the migration fixture.</summary>
internal static class SchemaV1
{
    public const string Ddl = """
        CREATE TABLE emails (
          id INTEGER PRIMARY KEY,
          entry_id TEXT NOT NULL UNIQUE, store_id TEXT, internet_message_id TEXT,
          folder TEXT NOT NULL, subject TEXT, sender_name TEXT, sender_email TEXT,
          to_addrs TEXT, cc_addrs TEXT, event_time INTEGER NOT NULL,
          last_modified INTEGER, unread INTEGER,
          has_attachments INTEGER,
          attachment_names TEXT,
          body TEXT);
        CREATE INDEX ix_emails_time ON emails(event_time);
        CREATE INDEX ix_emails_folder_time ON emails(folder COLLATE NOCASE, event_time, sender_name, sender_email);
        CREATE INDEX ix_emails_imid ON emails(internet_message_id);
        CREATE INDEX ix_emails_sender ON emails(sender_email);
        
        CREATE VIRTUAL TABLE emails_fts USING fts5(
          subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names,
          content='emails', content_rowid='id',
          tokenize='unicode61 remove_diacritics 2');
        
        CREATE TRIGGER emails_ai AFTER INSERT ON emails BEGIN
          INSERT INTO emails_fts(rowid, subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names)
          VALUES (new.id, new.subject, new.body, new.sender_name, new.sender_email, new.to_addrs, new.cc_addrs, new.attachment_names);
        END;
        CREATE TRIGGER emails_ad AFTER DELETE ON emails BEGIN
          INSERT INTO emails_fts(emails_fts, rowid, subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names)
          VALUES ('delete', old.id, old.subject, old.body, old.sender_name, old.sender_email, old.to_addrs, old.cc_addrs, old.attachment_names);
        END;
        CREATE TRIGGER emails_au AFTER UPDATE OF subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names ON emails BEGIN
          INSERT INTO emails_fts(emails_fts, rowid, subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names)
          VALUES ('delete', old.id, old.subject, old.body, old.sender_name, old.sender_email, old.to_addrs, old.cc_addrs, old.attachment_names);
          INSERT INTO emails_fts(rowid, subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names)
          VALUES (new.id, new.subject, new.body, new.sender_name, new.sender_email, new.to_addrs, new.cc_addrs, new.attachment_names);
        END;
        
        CREATE TABLE attachments (email_id INTEGER NOT NULL REFERENCES emails(id) ON DELETE CASCADE,
          position INTEGER NOT NULL, filename TEXT NOT NULL, size INTEGER, is_inline INTEGER,
          PRIMARY KEY (email_id, position));
        CREATE INDEX ix_att_name ON attachments(filename COLLATE NOCASE);
        
        CREATE TABLE sync_state (folder_key TEXT PRIMARY KEY,
          folder_path TEXT,
          modified_watermark INTEGER,
          oldest_synced INTEGER,
          last_reconcile INTEGER, backfill_complete INTEGER);
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
        """;

    internal sealed record V1Row(long Id, string EntryId, string Folder, string Subject, string Body, long EventTime,
        long LastModified, bool Unread, string? Imid, string[] AttachmentNames);

    public static V1Row[] Sample() =>
    [
        new(10, "A1", "Inbox", "Q3 budget review", "Please review the budget numbers.", 1_763_000_000, 1_763_000_100, true, "<a1@x>", ["a.pdf", "b.png"]),
        new(11, "A2", "Inbox", "Lunch", "Pizza on Friday?", 1_763_000_500, 1_763_000_600, false, "<a2@x>", []),
        new(12, "S1", "Sent Items", "Re:: budget", "Numbers look fine.", 1_763_001_000, 1_763_001_000, false, "<s1@x>", []),
        new(20, "S2", "Sent Items", "Hello", "Short note.", 1_763_002_000, 1_763_002_000, false, null, []),
    ];

    public static void Create(string dbPath, IEnumerable<V1Row> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        c.Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        using var tx = c.BeginTransaction();
        c.Execute(Ddl, transaction: tx);
        c.Execute("INSERT INTO meta(key, value) VALUES ('schema_version', '1')", transaction: tx);
        c.Execute("INSERT INTO meta(key, value) VALUES ('last_sync', '1763000000')", transaction: tx);
        c.Execute(
            "INSERT INTO sync_state (folder_key, folder_path, modified_watermark, oldest_synced, last_reconcile, backfill_complete) VALUES ('fk-inbox', 'Inbox', 1763000600, 1700000000, NULL, 1)",
            transaction: tx);
        foreach (var r in rows)
        {
            var names = string.Join("\n", r.AttachmentNames);
            c.Execute(
                """
                INSERT INTO emails (id, entry_id, store_id, internet_message_id, folder, subject, sender_name, sender_email,
                                    to_addrs, cc_addrs, event_time, last_modified, unread, has_attachments, attachment_names, body)
                VALUES (@Id, @EntryId, 'store', @Imid, @Folder, @Subject, 'Sender', 's@x.edu', 'to@x.edu', NULL,
                        @EventTime, @LastModified, @Unread, @Has, @Names, @Body)
                """,
                new
                {
                    r.Id, r.EntryId, r.Imid, r.Folder, r.Subject, r.EventTime, r.LastModified,
                    Unread = r.Unread ? 1 : 0, Has = r.AttachmentNames.Length > 0 ? 1 : 0,
                    Names = names.Length == 0 ? null : names, r.Body,
                }, tx);
            for (var i = 0; i < r.AttachmentNames.Length; i++)
                c.Execute("INSERT INTO attachments (email_id, position, filename, size, is_inline) VALUES (@Id, @p, @f, 100, 0)",
                    new { r.Id, p = i, f = r.AttachmentNames[i] }, tx);
        }
        tx.Commit();
        c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }
}
