using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using SQLitePCL;

namespace ClippysRevenge.Core;

/// <summary>Result of query_index (SPEC §11.12). Serialized by the tool layer as is.</summary>
public sealed record IndexQueryResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: System.Text.Json.Serialization.JsonPropertyName("rows")] IReadOnlyList<IReadOnlyList<object?>> Rows,
    [property: System.Text.Json.Serialization.JsonPropertyName("row_count")] int RowCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("truncated")] bool Truncated,
    [property: System.Text.Json.Serialization.JsonPropertyName("truncated_reason"),
        System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? TruncatedReason,
    [property: System.Text.Json.Serialization.JsonPropertyName("truncated_cells")] int TruncatedCells,
    [property: System.Text.Json.Serialization.JsonPropertyName("elapsed_ms")] long ElapsedMs);

/// <summary>User-facing texts of query_index (SPEC §11.12).</summary>
public static class IndexQueryTexts
{
    public const string SqlRequired = "sql is required: one SELECT over the views mail, mail_fts and mail_attachments.";
    public const string SqlTooLong = "sql is longer than 20,000 characters. Shorten the query.";
    public const string MultipleStatements = "query_index runs exactly one SELECT statement. Remove everything after the first statement.";
    public const string NoParameters = "query_index takes no parameters; write the values into the SQL.";
    public const string TooManyColumns = "query_index returns at most 100 columns.";
    public const string Timeout = "The query ran longer than 10 seconds and was stopped. Add a date range, a folder or a LIMIT, or use email_stats for counts.";

    public static string SqlError(string sqliteMessage)
    {
        var m = (sqliteMessage ?? "").Trim();
        if (m.EndsWith('.')) m = m[..^1].TrimEnd();
        if (m.Length == 0) m = "unknown error";
        return $"SQL error: {m}. query_index reads the views mail, mail_fts and mail_attachments (see the tool description).";
    }

    public static string Denied(string what) =>
        $"query_index only reads the views mail, mail_fts and mail_attachments with one SELECT. Not allowed here: {what}.";

    public static string Unavailable(string mailboxKey) =>
        $"The index for {(mailboxKey.Length == 0 ? "your own mailbox" : mailboxKey)} isn't available right now. Check index_status.";
    public const string ExampleTopSenders =
        "SELECT sender_email, count(*) AS n FROM mail WHERE folder = 'Inbox' AND unix_time >= strftime('%s','now','-1 year') GROUP BY sender_email ORDER BY n DESC LIMIT 10";
    public const string ExampleMailPerMonth =
        "SELECT substr(date,1,7) AS month, count(*) AS n FROM mail GROUP BY month ORDER BY month";
    public const string ExampleFullText =
        "SELECT m.id, m.date, m.subject, snippet(mail_fts, 1, '**', '**', '…', 12) AS hit FROM mail_fts JOIN mail m ON m.doc = mail_fts.doc WHERE mail_fts MATCH 'budget AND 2026' ORDER BY rank LIMIT 20";
    public const string ExamplePdfsBySender =
        "SELECT m.sender_email, count(*) AS n FROM mail_attachments a JOIN mail m ON m.doc = a.doc WHERE lower(a.filename) LIKE '%.pdf' GROUP BY 1 ORDER BY n DESC LIMIT 20";

    public static readonly IReadOnlyList<string> Examples =
        [ExampleTopSenders, ExampleMailPerMonth, ExampleFullText, ExamplePdfsBySender];

    public const string ToolDescription =
        "Runs one read-only SQLite SELECT over the local email index, for questions the other tools can't answer in one call: joins, custom grouping, full-text search with snippets. For plain counts by sender, domain, recipient, folder or time, email_stats is simpler. Works while Outlook is busy or closed; only synced mail is indexed.\n" +
        "Views:\n" +
        "mail(id, doc, mailbox, folder, date, unix_time, sender_name, sender_email, to_addrs, cc_addrs, subject, topic, body, unread, has_attachments, attachment_names): one row per email. id is the id get_message takes. doc joins to mail_fts.doc and mail_attachments.doc. mailbox is '' for the user's own mailbox. folder is a path like 'Inbox' or 'Sent Items'. date is local time as text 'yyyy-MM-ddTHH:mm:ss'; unix_time is UTC seconds. to_addrs and cc_addrs are 'Name <email>; ...'. topic is the conversation topic, body is plain text. unread and has_attachments are 0/1 (has_attachments ignores inline images). attachment_names has one file name per line.\n" +
        "mail_fts(mail_fts, rank, doc, subject, body, sender_name, sender_email, to_addrs, cc_addrs, attachment_names): the FTS5 full-text index. Filter with WHERE mail_fts MATCH '...' (FTS5 syntax: words are ANDed, OR, NOT, \"a phrase\", prefix*, column:word) and sort with ORDER BY rank (best first). snippet(mail_fts, n, ...) and highlight(mail_fts, n, ...) take a column number: 0 subject, 1 body, 2 sender_name, 3 sender_email, 4 to_addrs, 5 cc_addrs, 6 attachment_names. bm25(mail_fts) also works. Join to mail on doc.\n" +
        "mail_attachments(doc, position, filename, size, is_inline): one row per attachment. Only file names are indexed, never attachment content.\n" +
        "Limits: one read-only SELECT (WITH and VALUES also work), no ? parameters, 10 s, at most 1000 rows (max_rows), long text cut at 2,000 characters. Nothing else in the database can be read; PRAGMA, ATTACH and any write are refused. With mailbox=\"all\" each mailbox is queried separately and the rows are concatenated with a leading mailbox column, so counts and other aggregates are per mailbox; use email_stats for counts merged across mailboxes.\n" +
        "Returns {columns, rows (one array per row), row_count, truncated, truncated_reason, truncated_cells, elapsed_ms}.\n" +
        "Examples:\n" +
        "Top senders last year: " + ExampleTopSenders + "\n" +
        "Mail per month: " + ExampleMailPerMonth + "\n" +
        "Full text with snippets: " + ExampleFullText + "\n" +
        "PDFs by sender: " + ExamplePdfsBySender;

    public const string SqlParameterDescription =
        "One SQLite SELECT over the views mail, mail_fts and mail_attachments (see the tool description). Write values into the SQL; ? parameters are not supported. On large mailboxes add a LIMIT and a date or folder filter.";
    public const string MailboxParameterDescription =
        "Empty = the user's own mailbox, an SMTP address = that shared mailbox, 'all' = every enabled mailbox, each queried separately (rows get a leading mailbox column).";
    public const string MaxRowsParameterDescription = "Maximum rows returned, 1 to 1000.";
}

/// <summary>SPEC §11.12 authorizer as a pure function. Allow gives (true, null); deny gives (false, what).</summary>
public static class IndexQueryAuthorizer
{
    public readonly record struct Decision(bool Allow, string? What);

    private static readonly Decision Allowed = new(true, null);
    private static Decision Deny(string what) => new(false, what);

    private static readonly HashSet<string> ViewNames = new(StringComparer.OrdinalIgnoreCase)
        { "mail", "mail_fts", "mail_attachments" };

    private static readonly HashSet<string> FtsTables = new(StringComparer.OrdinalIgnoreCase)
        { "emails_fts", "emails_fts_data", "emails_fts_idx", "emails_fts_config", "emails_fts_docsize" };

    private static readonly HashSet<string> ViewEmailColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "id", "entry_id", "folder", "event_time", "sender_name", "sender_email", "to_addrs", "cc_addrs",
        "subject", "conversation_topic", "body", "unread", "has_attachments", "attachment_names",
    };

    private static readonly HashSet<string> ViewAttachmentColumns = new(StringComparer.OrdinalIgnoreCase)
        { "", "email_id", "position", "filename", "size", "is_inline" };

    private static readonly HashSet<string> DirectEmailColumns = new(StringComparer.OrdinalIgnoreCase)
        { "id", "subject", "body", "sender_name", "sender_email", "to_addrs", "cc_addrs", "attachment_names" };

    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "load_extension", "fts3_tokenizer", "readfile", "writefile", "edit" };

    /// <param name="action">SQLite action code (raw.SQLITE_*).</param>
    /// <param name="arg1">3rd C argument (table / pragma / ...).</param>
    /// <param name="arg2">4th C argument (column / function name / pragma value / ...).</param>
    /// <param name="dbName">"main", "temp", ... or null.</param>
    /// <param name="viewName">Inner-most trigger or view responsible, or null for top-level SQL.</param>
    public static Decision Decide(int action, string? arg1, string? arg2, string? dbName, string? viewName)
    {
        switch (action)
        {
            case raw.SQLITE_SELECT:
            case raw.SQLITE_RECURSIVE:
                return Allowed;
            case raw.SQLITE_READ:
                return DecideRead(arg1, arg2, dbName, viewName);
            case raw.SQLITE_FUNCTION:
                if (arg2 is null) return Deny("function (unknown)");
                if (DeniedFunctions.Contains(arg2)
                    || (arg2.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
                        && !arg2.Equals("sqlite_version", StringComparison.OrdinalIgnoreCase)))
                    return Deny("function " + arg2.ToLowerInvariant());
                return Allowed;
            case raw.SQLITE_PRAGMA:
                // FTS5 runs "PRAGMA main.data_version" itself (fts5IndexDataVersion). Users can't reach it:
                // PRAGMA statements are refused up front and pragma_* table functions fail the READ rules.
                return string.Equals(arg1, "data_version", StringComparison.OrdinalIgnoreCase)
                       && arg2 is null && viewName is null
                    ? Allowed
                    : Deny("PRAGMA");
            case raw.SQLITE_ATTACH:
                return Deny("ATTACH");
            case raw.SQLITE_DETACH:
                return Deny("DETACH");
            case raw.SQLITE_TRANSACTION:
            case raw.SQLITE_SAVEPOINT:
                return Deny("transactions");
            case raw.SQLITE_INSERT:
            case raw.SQLITE_UPDATE:
            case raw.SQLITE_ALTER_TABLE:
            case raw.SQLITE_REINDEX:
            case raw.SQLITE_ANALYZE:
            case raw.SQLITE_CREATE_VTABLE:
            case raw.SQLITE_DROP_VTABLE:
                return Deny("writing");
            case >= raw.SQLITE_CREATE_INDEX and <= raw.SQLITE_DROP_VIEW:   // 1..17: every CREATE_*/DROP_*
                return Deny("writing");
            default:
                return Deny("action " + action.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static readonly HashSet<string> HiddenTables = new(StringComparer.OrdinalIgnoreCase)
        { "meta", "sync_state", "ai_decisions", "dbstat" };

    private static Decision DecideRead(string? table, string? column, string? dbName, string? viewName)
    {
        var col = column ?? "";
        if (table is not null)
        {
            if (ViewNames.Contains(table)) return Allowed;
            if (table.Equals("json_each", StringComparison.OrdinalIgnoreCase)
                || table.Equals("json_tree", StringComparison.OrdinalIgnoreCase)) return Allowed;
            if (FtsTables.Contains(table)) return Allowed;
            var inView = viewName is not null && ViewNames.Contains(viewName);
            if (table.Equals("emails", StringComparison.OrdinalIgnoreCase))
            {
                if (inView && ViewEmailColumns.Contains(col)) return Allowed;
                if (DirectEmailColumns.Contains(col)) return Allowed;
            }
            else if (table.Equals("attachments", StringComparison.OrdinalIgnoreCase))
            {
                if (inView && ViewAttachmentColumns.Contains(col)) return Allowed;
            }
        }
        // A read with no column ("" : count(*), a constant-only select list, FROM x with no columns used) shows
        // at most that rows exist. SQLite reports it for CTE names (no schema to tell them apart), and for the
        // views' own "FROM main.emails" once flattened (dbName "main"; a user's plain "FROM emails" has none).
        if (table is not null && col.Length == 0)
        {
            var real = table.Equals("emails", StringComparison.OrdinalIgnoreCase)
                       || table.Equals("attachments", StringComparison.OrdinalIgnoreCase);
            if (real)
            {
                if (string.Equals(dbName, "main", StringComparison.OrdinalIgnoreCase)) return Allowed;
            }
            else if (!HiddenTables.Contains(table)
                     && !table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)
                     && !table.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
                return Allowed;
        }
        if (table is not null && col.Length > 0
            && (table.Equals("emails", StringComparison.OrdinalIgnoreCase)
                || table.Equals("attachments", StringComparison.OrdinalIgnoreCase)))
            return Deny("column " + table.ToLowerInvariant() + "." + col.ToLowerInvariant());
        return Deny("table " + (table ?? "(unknown)"));
    }
}

/// <summary>
/// query_index core (SPEC §11.12): one read-only SELECT over three temp views of a mailbox's index,
/// on a dedicated read-only, non-pooled connection with an authorizer, limits and a deadline.
/// Nothing here logs: the SQL text, cell values and SQLite messages never leave the result or the exception.
/// </summary>
public static class IndexQuery
{
    public const int MaxSqlChars = 20_000;
    public const int DefaultMaxRows = 200;
    public const int MaxRowsLimit = 1_000;
    public const int MaxColumns = 100;
    public const int MaxCellChars = 2_000;
    public const int MaxTotalChars = 1_000_000;
    public const int ProgressInterval = 1_000;
    public const int MaxValueBytes = 50_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const int DeadlineRowCheck = 100;

    public static IndexQueryResult Run(MailboxIndexes indexes, string? sql, string? mailbox,
        int maxRows = DefaultMaxRows, CancellationToken ct = default) =>
        Run(indexes, sql, mailbox, maxRows, Timeout, ct);

    internal static IndexQueryResult Run(MailboxIndexes indexes, string? sql, string? mailbox,
        int maxRows, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(sql)) throw new McpException(IndexQueryTexts.SqlRequired);
        if (sql.Length > MaxSqlChars) throw new McpException(IndexQueryTexts.SqlTooLong);

        var (kw, start) = LeadingKeyword(sql);
        if (start == sql.Length) throw new McpException(IndexQueryTexts.SqlRequired);
        switch (kw)
        {
            case "SELECT":
            case "WITH":
            case "VALUES":
                break;
            case "INSERT": case "REPLACE": case "UPDATE": case "DELETE": case "CREATE": case "DROP":
            case "ALTER": case "REINDEX": case "ANALYZE": case "VACUUM":
                throw new McpException(IndexQueryTexts.Denied("writing"));
            case "PRAGMA": throw new McpException(IndexQueryTexts.Denied("PRAGMA"));
            case "ATTACH": throw new McpException(IndexQueryTexts.Denied("ATTACH"));
            case "DETACH": throw new McpException(IndexQueryTexts.Denied("DETACH"));
            case "EXPLAIN": throw new McpException(IndexQueryTexts.Denied("EXPLAIN"));
            case "BEGIN": case "COMMIT": case "END": case "ROLLBACK": case "SAVEPOINT": case "RELEASE":
                throw new McpException(IndexQueryTexts.Denied("transactions"));
            default:
                throw new McpException(IndexQueryTexts.Denied("statements that don't start with SELECT, WITH or VALUES"));
        }

        maxRows = Math.Clamp(maxRows, 1, MaxRowsLimit);

        var key = MailboxIndexes.Normalize(mailbox);
        var withMailbox = key == MailboxIndexes.AllMailboxes;
        IReadOnlyList<string> targets;
        if (withMailbox) targets = indexes.Mailboxes;
        else
        {
            indexes.For(mailbox);
            targets = [key];
        }
        var paths = targets.Select(t => indexes.For(t).DbPath).ToArray();

        var text = sql.Substring(start);
        var state = new RunState(maxRows);
        for (var i = 0; i < targets.Count; i++)
        {
            if (state.Rows.Count >= maxRows)
            {
                // Rows are full and mailboxes remain unqueried.
                state.Truncated = true;
                state.Reason = "max_rows";
                break;
            }
            if (sw.Elapsed >= timeout) throw new McpException(IndexQueryTexts.Timeout);
            RunOne(targets[i], paths[i], text, withMailbox, state, sw, timeout, ct);
            if (state.Truncated) break;
            if (sw.Elapsed >= timeout) throw new McpException(IndexQueryTexts.Timeout);
        }

        var columns = withMailbox && state.Columns is not null
            ? new[] { "mailbox" }.Concat(state.Columns).ToList()
            : state.Columns ?? [];
        return new IndexQueryResult(columns, state.Rows, state.Rows.Count, state.Truncated, state.Reason,
            state.TruncatedCells, sw.ElapsedMilliseconds);
    }

    private sealed class RunState(int maxRows)
    {
        public int MaxRows { get; } = maxRows;
        public List<IReadOnlyList<object?>> Rows { get; } = [];
        public List<string>? Columns { get; set; }
        public bool Truncated { get; set; }
        public string? Reason { get; set; }
        public int TruncatedCells { get; set; }
        public long TotalChars { get; set; }
    }

    private sealed class CallState
    {
        public string? Denied;
        public volatile bool InterruptFired;
    }

    private static void RunOne(string key, string dbPath, string sql, bool withMailbox, RunState state,
        Stopwatch sw, TimeSpan timeout, CancellationToken ct)
    {
        if (!File.Exists(dbPath)) throw new McpException(IndexQueryTexts.Unavailable(key));

        SqliteConnection? connection = null;
        try
        {
            try
            {
                connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
                connection.Open();
                Exec(connection, "PRAGMA busy_timeout=5000;");
                // Still without the authorizer: the file must be a Clippy index.
                using (var check = connection.CreateCommand())
                {
                    check.CommandText =
                        "SELECT count(*) FROM main.sqlite_master WHERE type IN ('table') AND name IN ('emails','attachments','emails_fts')";
                    if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 3)
                        throw new McpException(IndexQueryTexts.Unavailable(key));
                }
                Exec(connection, ViewDdl(key));
                Exec(connection, "PRAGMA query_only = ON;");
            }
            catch (SqliteException)
            {
                // Rebuilding, locked past the busy timeout, not a database. The message holds the file path.
                throw new McpException(IndexQueryTexts.Unavailable(key));
            }

            var db = connection.Handle!;
            raw.sqlite3_limit(db, raw.SQLITE_LIMIT_ATTACHED, 0);
            raw.sqlite3_limit(db, raw.SQLITE_LIMIT_SQL_LENGTH, MaxSqlChars);
            raw.sqlite3_limit(db, raw.SQLITE_LIMIT_LENGTH, MaxValueBytes);
            raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_ENABLE_LOAD_EXTENSION, 0, out _);

            var call = new CallState();
            strdelegate_authorizer authorizer = (_, action, p0, p1, dbName, view) =>
            {
                try
                {
                    var d = IndexQueryAuthorizer.Decide(action, p0, p1, dbName, view);
                    if (d.Allow) return raw.SQLITE_OK;
                    call.Denied ??= d.What;
                    return raw.SQLITE_DENY;
                }
                catch
                {
                    call.Denied ??= "action " + action.ToString(CultureInfo.InvariantCulture);
                    return raw.SQLITE_DENY;
                }
            };
            delegate_progress progress = _ =>
            {
                if (ct.IsCancellationRequested || sw.Elapsed >= timeout)
                {
                    call.InterruptFired = true;
                    return 1;
                }
                return 0;
            };
            raw.sqlite3_set_authorizer(db, authorizer, null);
            raw.sqlite3_progress_handler(db, ProgressInterval, progress, null);

            sqlite3_stmt? stmt = null;
            try
            {
                Execute(db, sql, key, withMailbox, state, sw, timeout, ct, call, ref stmt);
            }
            finally
            {
                stmt?.Dispose();
                GC.KeepAlive(authorizer);
                GC.KeepAlive(progress);
            }
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private static void Exec(SqliteConnection connection, string commandText)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = commandText;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(sqlite3 db, string sql, string key, bool withMailbox, RunState state,
        Stopwatch sw, TimeSpan timeout, CancellationToken ct, CallState call, ref sqlite3_stmt? stmtRef)
    {
        call.Denied = null;
        var rc = raw.sqlite3_prepare_v2(db, sql, out var stmt, out var tail);
        stmtRef = stmt;
        if (rc != raw.SQLITE_OK) throw Failure(db, call);
        if (stmt is null || stmt.IsInvalid) throw new McpException(IndexQueryTexts.SqlRequired);
        if (!IndexQuery.IsEmptyTail(tail)) throw new McpException(IndexQueryTexts.MultipleStatements);
        if (raw.sqlite3_stmt_readonly(stmt) == 0) throw new McpException(IndexQueryTexts.Denied("writing"));
        if (raw.sqlite3_bind_parameter_count(stmt) > 0) throw new McpException(IndexQueryTexts.NoParameters);
        var colCount = raw.sqlite3_column_count(stmt);
        if (colCount > MaxColumns) throw new McpException(IndexQueryTexts.TooManyColumns);

        if (state.Columns is null)
        {
            var names = new List<string>(colCount);
            for (var i = 0; i < colCount; i++)
                names.Add(raw.sqlite3_column_name(stmt, i).utf8_to_string() ?? "");
            state.Columns = names;
        }

        var stepped = 0;
        while (true)
        {
            rc = raw.sqlite3_step(stmt);
            if (rc == raw.SQLITE_DONE) return;
            if (rc != raw.SQLITE_ROW)
            {
                if (rc == raw.SQLITE_INTERRUPT || call.InterruptFired)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw new McpException(IndexQueryTexts.Timeout);
                }
                throw Failure(db, call);
            }

            if (state.Rows.Count >= state.MaxRows)
            {
                state.Truncated = true;
                state.Reason = "max_rows";
                return;
            }
            var cutCells = 0;
            var row = BuildRow(stmt, colCount, key, withMailbox, out var rowChars, ref cutCells);
            if (state.TotalChars + rowChars > MaxTotalChars)
            {
                state.Truncated = true;
                state.Reason = "size";
                return;
            }
            state.TotalChars += rowChars;
            state.TruncatedCells += cutCells;
            state.Rows.Add(row);

            if (++stepped % DeadlineRowCheck == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed >= timeout) throw new McpException(IndexQueryTexts.Timeout);
            }
        }
    }

    private static McpException Failure(sqlite3 db, CallState call) =>
        call.Denied is { } what
            ? new McpException(IndexQueryTexts.Denied(what))
            : new McpException(IndexQueryTexts.SqlError(raw.sqlite3_errmsg(db).utf8_to_string() ?? ""));

    private static IReadOnlyList<object?> BuildRow(sqlite3_stmt stmt, int colCount, string key, bool withMailbox,
        out long rowChars, ref int cutCells)
    {
        var row = new List<object?>(colCount + 1);
        if (withMailbox) row.Add(key);
        long chars = 0;
        for (var i = 0; i < colCount; i++)
        {
            object? cell;
            switch (raw.sqlite3_column_type(stmt, i))
            {
                case raw.SQLITE_INTEGER:
                    cell = raw.sqlite3_column_int64(stmt, i);
                    break;
                case raw.SQLITE_FLOAT:
                    var d = raw.sqlite3_column_double(stmt, i);
                    cell = double.IsFinite(d) ? d : null;
                    break;
                case raw.SQLITE_TEXT:
                    var s = raw.sqlite3_column_text(stmt, i).utf8_to_string() ?? "";
                    if (s.Length > MaxCellChars)
                    {
                        var cut = char.IsHighSurrogate(s[MaxCellChars - 1]) ? MaxCellChars - 1 : MaxCellChars;
                        s = s[..cut] + "\u2026";
                        cutCells++;
                    }
                    chars += s.Length;
                    cell = s;
                    break;
                case raw.SQLITE_BLOB:
                    var blob = "[blob, " + raw.sqlite3_column_bytes(stmt, i).ToString(CultureInfo.InvariantCulture) + " bytes]";
                    chars += blob.Length;
                    cell = blob;
                    break;
                default:
                    cell = null;
                    break;
            }
            row.Add(cell);
        }
        rowChars = chars;
        return row;
    }

    /// <summary>The CREATE TEMP VIEW script for one mailbox key ("" = own); the key is a doubled-quote SQL literal.</summary>
    internal static string ViewDdl(string mailboxKey)
    {
        var mb = (mailboxKey ?? "").Replace("'", "''");
        return $$"""
            CREATE TEMP VIEW mail AS
            SELECT e.entry_id AS id,
                   e.id AS doc,
                   '{{mb}}' AS mailbox,
                   e.folder AS folder,
                   strftime('%Y-%m-%dT%H:%M:%S', e.event_time, 'unixepoch', 'localtime') AS date,
                   e.event_time AS unix_time,
                   e.sender_name AS sender_name,
                   e.sender_email AS sender_email,
                   e.to_addrs AS to_addrs,
                   e.cc_addrs AS cc_addrs,
                   e.subject AS subject,
                   e.conversation_topic AS topic,
                   e.body AS body,
                   COALESCE(e.unread, 0) AS unread,
                   COALESCE(e.has_attachments, 0) AS has_attachments,
                   e.attachment_names AS attachment_names
            FROM main.emails AS e;

            CREATE TEMP VIEW mail_fts AS
            SELECT f.emails_fts AS mail_fts,
                   f.rank AS rank,
                   f.rowid AS doc,
                   f.subject AS subject,
                   f.body AS body,
                   f.sender_name AS sender_name,
                   f.sender_email AS sender_email,
                   f.to_addrs AS to_addrs,
                   f.cc_addrs AS cc_addrs,
                   f.attachment_names AS attachment_names
            FROM main.emails_fts AS f;

            CREATE TEMP VIEW mail_attachments AS
            SELECT a.email_id AS doc,
                   a.position AS position,
                   a.filename AS filename,
                   a.size AS size,
                   COALESCE(a.is_inline, 0) AS is_inline
            FROM main.attachments AS a;
            """;
    }

    /// <summary>Skips whitespace, ';', "--" line comments and "/* */" comments (an unterminated "/*" runs to the end).
    /// Returns the first word (ASCII letters, digits, _) upper-cased, "" when nothing is left or the next char isn't a
    /// word char, and the index where that first token starts (sql.Length when nothing is left).</summary>
    internal static (string Keyword, int Start) LeadingKeyword(string sql)
    {
        var i = 0;
        var n = sql.Length;
        while (i < n)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c) || c == ';') { i++; continue; }
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i + 2);
                i = nl < 0 ? n : nl + 1;
                continue;
            }
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }
            break;
        }
        if (i >= n) return ("", n);
        var s = i;
        while (i < n && IsWordChar(sql[i])) i++;
        return (sql.Substring(s, i - s).ToUpperInvariant(), s);
    }

    private static bool IsWordChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    /// <summary>True when the text is only whitespace, ';' and comments.</summary>
    internal static bool IsEmptyTail(string? tail) =>
        string.IsNullOrEmpty(tail) || LeadingKeyword(tail).Start == tail.Length;
}
