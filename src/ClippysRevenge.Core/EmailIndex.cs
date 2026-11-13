using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core;

/// <summary>SQLite + FTS5 index for one mailbox (SPEC §9).</summary>
public sealed class EmailIndex
{
    public const int SchemaVersion = 1;

    private const string Step1Ddl = """
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
        CREATE INDEX ix_emails_folder_time ON emails(folder, event_time);
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

    // Step n upgrades n-1 -> n.
    private static readonly string[] Migrations = [Step1Ddl];

    private const string UpsertSql = """
        INSERT INTO emails (entry_id, store_id, internet_message_id, folder, subject, sender_name, sender_email,
                            to_addrs, cc_addrs, event_time, last_modified, unread, has_attachments, attachment_names, body)
        VALUES (@EntryId, @StoreId, @InternetMessageId, @Folder, @Subject, @SenderName, @SenderEmail,
                @To, @Cc, @EventTime, @LastModified, @Unread, @HasAttachments, @AttachmentNames, @Body)
        ON CONFLICT(entry_id) DO UPDATE SET
          store_id = excluded.store_id,
          internet_message_id = excluded.internet_message_id,
          folder = excluded.folder,
          subject = excluded.subject,
          sender_name = excluded.sender_name,
          sender_email = excluded.sender_email,
          to_addrs = excluded.to_addrs,
          cc_addrs = excluded.cc_addrs,
          event_time = excluded.event_time,
          last_modified = excluded.last_modified,
          unread = excluded.unread,
          has_attachments = excluded.has_attachments,
          attachment_names = excluded.attachment_names,
          body = excluded.body
        RETURNING id
        """;

    public EmailIndex(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        DbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var current = ReadVersion(conn, tx);
        if (current < SchemaVersion)
        {
            for (var n = current + 1; n <= SchemaVersion; n++)
                conn.Execute(Migrations[n - 1], transaction: tx);
            conn.Execute(
                "INSERT INTO meta(key, value) VALUES ('schema_version', @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { v = SchemaVersion.ToString(CultureInfo.InvariantCulture) }, tx);
        }
        tx.Commit();
    }

    public string DbPath { get; }
    internal string ConnectionString { get; }

    private int ReadVersion(SqliteConnection conn, SqliteTransaction tx)
    {
        var hasMeta = conn.ExecuteScalar<long?>(
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='meta'", transaction: tx);
        if (hasMeta is null) return 0;
        var raw = conn.ExecuteScalar<string?>(
            "SELECT value FROM meta WHERE key='schema_version'", transaction: tx);
        if (raw is null) return 0;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v > SchemaVersion)
            throw new InvalidOperationException(
                $"Index database '{DbPath}' has schema version {raw}, but this version of Clippy's Revenge supports up to {SchemaVersion}. Update Clippy's Revenge or rebuild the index.");
        return v;
    }

    /// <summary>Opens a pooled connection and applies the §9.2 pragmas. Caller disposes.</summary>
    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        try
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public void UpsertBatch(IReadOnlyList<IndexedEmail> emails)
    {
        ArgumentNullException.ThrowIfNull(emails);
        foreach (var e in emails) Validate(e);
        if (emails.Count == 0) return;

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var e in emails)
        {
            var atts = e.Attachments.OrderBy(a => a.Position).ToList();
            var names = string.Join("\n", atts.Select(a => a.FileName).Where(n => n.Length > 0));
            var id = conn.ExecuteScalar<long>(UpsertSql, new
            {
                e.EntryId,
                e.StoreId,
                e.InternetMessageId,
                e.Folder,
                e.Subject,
                e.SenderName,
                e.SenderEmail,
                e.To,
                e.Cc,
                EventTime = e.EventTime.ToUnixTimeSeconds(),
                LastModified = e.LastModified?.ToUnixTimeSeconds(),
                Unread = e.Unread ? 1 : 0,
                HasAttachments = atts.Any(a => !a.IsInline) ? 1 : 0,
                AttachmentNames = names.Length == 0 ? null : names,
                e.Body,
            }, tx);
            conn.Execute("DELETE FROM attachments WHERE email_id = @id", new { id }, tx);
            foreach (var a in atts)
                conn.Execute(
                    "INSERT INTO attachments (email_id, position, filename, size, is_inline) VALUES (@id, @p, @f, @s, @i)",
                    new { id, p = a.Position, f = a.FileName, s = a.Size, i = a.IsInline ? 1 : 0 }, tx);
        }
        tx.Commit();
    }

    private static void Validate(IndexedEmail? e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (string.IsNullOrWhiteSpace(e.EntryId))
            throw new ArgumentException("Email has an empty entry id.");
        if (string.IsNullOrWhiteSpace(e.Folder))
            throw new ArgumentException($"Email '{e.EntryId}' has an empty folder.");
        if (e.Attachments is null)
            throw new ArgumentException($"Email '{e.EntryId}' has null attachments.");
        var seen = new HashSet<int>();
        foreach (var a in e.Attachments)
        {
            if (a is null)
                throw new ArgumentException($"Email '{e.EntryId}' has a null attachment.");
            if (a.FileName is null)
                throw new ArgumentException($"Email '{e.EntryId}' has an attachment with a null file name.");
            if (!seen.Add(a.Position))
                throw new ArgumentException($"Email '{e.EntryId}' has duplicate attachment position {a.Position}.");
        }
    }

    public int Delete(IEnumerable<string> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var ids = entryIds.Where(i => !string.IsNullOrEmpty(i)).ToList();
        if (ids.Count == 0) return 0;
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var total = 0;
        foreach (var id in ids)
            total += conn.Execute("DELETE FROM emails WHERE entry_id = @e", new { e = id }, tx);
        tx.Commit();
        return total;
    }

    public int SetUnread(IEnumerable<(string entryId, bool unread)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var list = changes.Where(c => !string.IsNullOrEmpty(c.entryId)).ToList();
        if (list.Count == 0) return 0;
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var total = 0;
        foreach (var (entryId, unread) in list)
            total += conn.Execute("UPDATE emails SET unread = @u WHERE entry_id = @e",
                new { u = unread ? 1 : 0, e = entryId }, tx);
        tx.Commit();
        return total;
    }

    public EmailDetail? Get(string entryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryId);
        using var conn = OpenConnection();
        var row = conn.QueryFirstOrDefault<EmailRow>(
            """
            SELECT id AS Id, entry_id AS EntryId, store_id AS StoreId, internet_message_id AS InternetMessageId,
                   folder AS Folder, subject AS Subject, sender_name AS SenderName, sender_email AS SenderEmail,
                   to_addrs AS ToAddrs, cc_addrs AS CcAddrs, event_time AS EventTime, last_modified AS LastModified,
                   unread AS Unread, has_attachments AS HasAttachments, body AS Body
            FROM emails WHERE entry_id = @entryId
            """, new { entryId });
        if (row is null) return null;
        var atts = conn.Query<AttRow>(
            "SELECT position AS Position, filename AS FileName, size AS Size, is_inline AS IsInline FROM attachments WHERE email_id = @id ORDER BY position",
            new { id = row.Id })
            .Select(a => new AttachmentInfo((int)a.Position, a.FileName, a.Size, (a.IsInline ?? 0) != 0))
            .ToList();
        return new EmailDetail
        {
            RowId = row.Id,
            EntryId = row.EntryId,
            StoreId = row.StoreId,
            InternetMessageId = row.InternetMessageId,
            Folder = row.Folder,
            Subject = row.Subject,
            SenderName = row.SenderName,
            SenderEmail = row.SenderEmail,
            To = row.ToAddrs,
            Cc = row.CcAddrs,
            EventTime = DateTimeOffset.FromUnixTimeSeconds(row.EventTime),
            LastModified = row.LastModified is { } lm ? DateTimeOffset.FromUnixTimeSeconds(lm) : null,
            Unread = (row.Unread ?? 0) != 0,
            HasAttachments = (row.HasAttachments ?? 0) != 0,
            Attachments = atts,
            Body = row.Body,
        };
    }

    private sealed class EmailRow
    {
        public long Id { get; set; }
        public string EntryId { get; set; } = "";
        public string? StoreId { get; set; }
        public string? InternetMessageId { get; set; }
        public string Folder { get; set; } = "";
        public string? Subject { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmail { get; set; }
        public string? ToAddrs { get; set; }
        public string? CcAddrs { get; set; }
        public long EventTime { get; set; }
        public long? LastModified { get; set; }
        public long? Unread { get; set; }
        public long? HasAttachments { get; set; }
        public string? Body { get; set; }
    }

    private sealed class AttRow
    {
        public long Position { get; set; }
        public string FileName { get; set; } = "";
        public long? Size { get; set; }
        public long? IsInline { get; set; }
    }
}
