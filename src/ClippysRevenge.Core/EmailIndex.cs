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

    // ---- Purge, status, sync state, close (SPEC §9.2) ----

    /// <summary>Deletes emails with event_time before <paramref name="cutoff"/>; VACUUMs when more than 10% of rows went.</summary>
    public int PurgeOlderThan(DateTimeOffset cutoff)
    {
        using var conn = OpenConnection();
        int removed;
        long before;
        using (var tx = conn.BeginTransaction())
        {
            before = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM emails", transaction: tx);
            removed = conn.Execute("DELETE FROM emails WHERE event_time < @c", new { c = cutoff.ToUnixTimeSeconds() }, tx);
            tx.Commit();
        }
        if (removed > 0 && removed * 10L > before)
            conn.Execute("VACUUM");
        return removed;
    }

    public IndexStatus Status()
    {
        using var conn = OpenConnection();
        var folders = conn.Query<FolderCountRow>(
            "SELECT folder AS Folder, COUNT(*) AS N FROM emails GROUP BY folder ORDER BY folder")
            .ToDictionary(r => r.Folder, r => r.N, StringComparer.Ordinal);
        var range = conn.QuerySingle<RangeRow>("SELECT MIN(event_time) AS Min, MAX(event_time) AS Max FROM emails");
        var sync = conn.QuerySingle<SyncSummaryRow>(
            "SELECT MAX(last_reconcile) AS Last, COUNT(*) AS Folders, COALESCE(SUM(COALESCE(backfill_complete, 0) = 0), 0) AS Incomplete FROM sync_state");
        long? metaLastSync = long.TryParse(
            conn.ExecuteScalar<string?>("SELECT value FROM meta WHERE key = 'last_sync'"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var ls) ? ls : null;
        long size = 0;
        foreach (var f in new[] { DbPath, DbPath + "-wal" })
            if (File.Exists(f)) size += new FileInfo(f).Length;
        return new IndexStatus(
            folders.Values.Sum(),
            folders,
            FromUnix(range.Min),
            FromUnix(range.Max),
            size,
            FromUnix(metaLastSync ?? sync.Last),
            sync.Folders > 0 && sync.Incomplete == 0);
    }

    public string? GetMeta(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        using var conn = OpenConnection();
        return conn.ExecuteScalar<string?>("SELECT value FROM meta WHERE key = @key", new { key });
    }

    /// <summary>Null deletes the key. "schema_version" can't be written.</summary>
    public void SetMeta(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key == "schema_version")
            throw new ArgumentException("schema_version is managed by the index and can't be written.", nameof(key));
        using var conn = OpenConnection();
        if (value is null)
            conn.Execute("DELETE FROM meta WHERE key = @key", new { key });
        else
            conn.Execute(
                "INSERT INTO meta(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { key, value });
    }

    /// <summary>Known ids only; unknown ids are absent.</summary>
    public IReadOnlyDictionary<string, IndexedRowInfo> GetSyncInfo(IReadOnlyCollection<string> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        var result = new Dictionary<string, IndexedRowInfo>(StringComparer.Ordinal);
        var ids = entryIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return result;
        using var conn = OpenConnection();
        foreach (var chunk in ids.Chunk(500))
        {
            var sql = "SELECT entry_id AS EntryId, last_modified AS LastModified, unread AS Unread FROM emails WHERE entry_id IN ("
                      + string.Join(",", chunk.Select((_, i) => "@p" + i.ToString(CultureInfo.InvariantCulture))) + ")";
            var p = new DynamicParameters();
            for (var i = 0; i < chunk.Length; i++) p.Add("p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
            foreach (var r in conn.Query<InfoRow>(sql, p))
                result[r.EntryId] = new IndexedRowInfo(FromUnix(r.LastModified), (r.Unread ?? 0) != 0);
        }
        return result;
    }

    private sealed class InfoRow
    {
        public string EntryId { get; set; } = "";
        public long? LastModified { get; set; }
        public long? Unread { get; set; }
    }

    public SyncState? GetSyncState(string folderKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderKey);
        using var conn = OpenConnection();
        var r = conn.QueryFirstOrDefault<SyncRow>(
            "SELECT folder_key AS FolderKey, folder_path AS FolderPath, modified_watermark AS W, oldest_synced AS O, last_reconcile AS L, backfill_complete AS B FROM sync_state WHERE folder_key = @folderKey",
            new { folderKey });
        if (r is null) return null;
        return new SyncState(r.FolderKey, r.FolderPath, FromUnix(r.W), FromUnix(r.O), FromUnix(r.L), (r.B ?? 0) != 0);
    }

    public void SetSyncState(SyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrEmpty(state.FolderKey);
        using var conn = OpenConnection();
        conn.Execute(
            """
            INSERT INTO sync_state (folder_key, folder_path, modified_watermark, oldest_synced, last_reconcile, backfill_complete)
            VALUES (@k, @p, @w, @o, @l, @b)
            ON CONFLICT(folder_key) DO UPDATE SET folder_path = excluded.folder_path,
              modified_watermark = excluded.modified_watermark, oldest_synced = excluded.oldest_synced,
              last_reconcile = excluded.last_reconcile, backfill_complete = excluded.backfill_complete
            """,
            new
            {
                k = state.FolderKey,
                p = state.FolderPath,
                w = state.ModifiedWatermark?.ToUnixTimeSeconds(),
                o = state.OldestSynced?.ToUnixTimeSeconds(),
                l = state.LastReconcile?.ToUnixTimeSeconds(),
                b = state.BackfillComplete ? 1 : 0,
            });
    }

    private static DateTimeOffset? FromUnix(long? v) => v is { } x ? DateTimeOffset.FromUnixTimeSeconds(x) : null;

    /// <summary>Releases pooled connections so the file can be deleted (rebuild, mailbox removal).</summary>
    public void Close()
    {
        try
        {
            using var conn = OpenConnection();
            conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (SqliteException) { }
        using var c = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(c);
    }

    private sealed class FolderCountRow
    {
        public string Folder { get; set; } = "";
        public long N { get; set; }
    }

    private sealed class RangeRow
    {
        public long? Min { get; set; }
        public long? Max { get; set; }
    }

    private sealed class SyncSummaryRow
    {
        public long? Last { get; set; }
        public long Folders { get; set; }
        public long Incomplete { get; set; }
    }

    private sealed class SyncRow
    {
        public string FolderKey { get; set; } = "";
        public string? FolderPath { get; set; }
        public long? W { get; set; }
        public long? O { get; set; }
        public long? L { get; set; }
        public long? B { get; set; }
    }

    // ---- Search (SPEC §9.2, §11.1) ----

    public IReadOnlyList<EmailSummary> Search(SearchFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return SearchPage(filter,
            Math.Max(filter.Offset, 0),
            Math.Clamp(filter.Limit, 1, SearchFilter.MaxLimit));
    }

    internal IReadOnlyList<EmailSummary> SearchPage(SearchFilter filter, int skip, int take)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        if (take == 0) return [];

        var p = new DynamicParameters();
        var where = new List<string>();
        var fts = FtsQuery.Build(filter.Query);
        string from;
        if (fts is not null)
        {
            from = "emails_fts JOIN emails e ON e.id = emails_fts.rowid";
            where.Add("emails_fts MATCH @q");
            p.Add("q", fts);
        }
        else
        {
            from = "emails e";
        }

        if (Trimmed(filter.Sender) is { } sender)
        {
            where.Add("(e.sender_name LIKE @s ESCAPE '\\' OR e.sender_email LIKE @s ESCAPE '\\')");
            p.Add("s", Like(sender));
        }
        AddRecipient(filter.Recipient, where, p);
        if (Trimmed(filter.Subject) is { } subject)
        {
            where.Add("e.subject LIKE @subj ESCAPE '\\'");
            p.Add("subj", Like(subject));
        }
        if (filter.DateFrom is { } df)
        {
            where.Add("e.event_time >= @from");
            p.Add("from", df.ToUnixTimeSeconds());
        }
        if (filter.DateTo is { } dtTo)
        {
            where.Add("e.event_time <= @to");
            p.Add("to", dtTo.ToUnixTimeSeconds());
        }
        if (NormalizeFolder(filter.Folder) is { } folder)
        {
            where.Add("e.folder = @folder COLLATE NOCASE");
            p.Add("folder", folder);
        }
        if (filter.HasAttachment is { } ha)
        {
            where.Add("COALESCE(e.has_attachments, 0) = @ha");
            p.Add("ha", ha ? 1 : 0);
        }
        if (Trimmed(filter.AttachmentName) is { } an)
        {
            where.Add("EXISTS (SELECT 1 FROM attachments a WHERE a.email_id = e.id AND a.filename LIKE @an ESCAPE '\\')");
            p.Add("an", Like(an));
        }
        if (Trimmed(filter.AttachmentExt) is { } rawExt)
        {
            var ext = rawExt.StartsWith('.') ? rawExt[1..] : rawExt;
            if (ext.Length > 0)
            {
                where.Add("EXISTS (SELECT 1 FROM attachments a WHERE a.email_id = e.id AND a.filename LIKE @ext ESCAPE '\\')");
                p.Add("ext", "%." + Escape(ext));
            }
        }
        if (filter.Unread is { } un)
        {
            where.Add("COALESCE(e.unread, 0) = @u");
            p.Add("u", un ? 1 : 0);
        }

        // Pick the page of ids first; compute snippet/attachments/body only for those rows.
        var snippetCol = fts is not null
            ? "snippet(emails_fts, -1, '**', '**', '…', 16)"
            : "NULL";
        var outerFrom = fts is not null
            ? "page JOIN emails e ON e.id = page.id JOIN emails_fts ON emails_fts.rowid = e.id AND emails_fts MATCH @q"
            : "page JOIN emails e ON e.id = page.id";
        var sql = "WITH page AS (SELECT e.id AS id, e.event_time AS t FROM " + from +
                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  " ORDER BY e.event_time DESC, e.id DESC LIMIT @take OFFSET @skip) " +
                  "SELECT e.entry_id AS EntryId, e.folder AS Folder, e.event_time AS EventTime, " +
                  "e.sender_name AS SenderName, e.sender_email AS SenderEmail, e.to_addrs AS ToAddrs, " +
                  "e.subject AS Subject, e.has_attachments AS HasAttachments, " +
                  "(SELECT json_group_array(json_object('p', a.position, 'n', a.filename, 's', a.size, 'i', a.is_inline)) " +
                  "FROM attachments a WHERE a.email_id = e.id) AS AttJson, " +
                  snippetCol + " AS Fts, substr(e.body, 1, 1000) AS Body FROM " + outerFrom +
                  " ORDER BY page.t DESC, page.id DESC";
        p.Add("take", take);
        p.Add("skip", skip);

        using var conn = OpenConnection();
        var rows = conn.Query<SearchRow>(sql, p).ToList();
        var result = new List<EmailSummary>(rows.Count);
        foreach (var r in rows)
        {
            var snippet = r.Fts is null ? "" : CollapseWhitespace(r.Fts);
            if (snippet.Length == 0) snippet = BodySnippet(r.Body);
            result.Add(new EmailSummary(
                r.EntryId,
                r.Folder ?? "",
                DateTimeOffset.FromUnixTimeSeconds(r.EventTime),
                r.SenderName ?? "",
                r.SenderEmail ?? "",
                r.ToAddrs ?? "",
                r.Subject ?? "",
                (r.HasAttachments ?? 0) != 0,
                ParseAttachments(r.AttJson),
                snippet));
        }
        return result;
    }

    private static string? Trimmed(string? s)
    {
        var t = s?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Like(string s) => "%" + Escape(s) + "%";

    private static void AddRecipient(string? raw, List<string> where, DynamicParameters p)
    {
        var value = Trimmed(raw);
        if (value is null) return;

        IReadOnlyList<Recipient>? parsed = null;
        if (value.Contains(';') || value.Contains('<'))
        {
            try { parsed = Recipients.Parse(value); }
            catch (FormatException) { }
            if (parsed is { Count: 0 }) parsed = null;
        }

        if (parsed is null)
        {
            where.Add("(e.to_addrs LIKE @r0 ESCAPE '\\' OR e.cc_addrs LIKE @r0 ESCAPE '\\')");
            p.Add("r0", Like(value));
            return;
        }

        var parts = new List<string>();
        var i = 0;
        foreach (var r in parsed.Take(50))
        {
            var n = "r" + (i++).ToString(CultureInfo.InvariantCulture);
            parts.Add($"(e.to_addrs LIKE @{n} ESCAPE '\\' OR e.cc_addrs LIKE @{n} ESCAPE '\\')");
            p.Add(n, Like(r.Email));
        }
        where.Add("(" + string.Join(" OR ", parts) + ")");
    }

    private static string? NormalizeFolder(string? folder)
    {
        var f = folder?.Trim().Replace('/', '\\').Trim('\\');
        if (string.IsNullOrEmpty(f)) return null;
        if (f.Equals("inbox", StringComparison.OrdinalIgnoreCase)) return "Inbox";
        if (f.Equals("sent", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("sent items", StringComparison.OrdinalIgnoreCase)) return "Sent Items";
        return f;
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string BodySnippet(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var s = CollapseWhitespace(body);
        if (s.Length <= 200) return s;
        var cut = char.IsHighSurrogate(s[199]) ? 199 : 200;
        return s[..cut];
    }

    private static IReadOnlyList<AttachmentInfo> ParseAttachments(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        var list = new List<AttachmentInfo>();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            var size = el.GetProperty("s");
            var inl = el.GetProperty("i");
            list.Add(new AttachmentInfo(
                el.GetProperty("p").GetInt32(),
                el.GetProperty("n").GetString() ?? "",
                size.ValueKind == System.Text.Json.JsonValueKind.Number ? size.GetInt64() : 0,
                inl.ValueKind == System.Text.Json.JsonValueKind.Number && inl.GetInt64() != 0));
        }
        return list.OrderBy(a => a.Position).ToList();
    }

    private sealed class SearchRow
    {
        public string EntryId { get; set; } = "";
        public string? Folder { get; set; }
        public long EventTime { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmail { get; set; }
        public string? ToAddrs { get; set; }
        public string? Subject { get; set; }
        public long? HasAttachments { get; set; }
        public string? AttJson { get; set; }
        public string? Fts { get; set; }
        public string? Body { get; set; }
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
