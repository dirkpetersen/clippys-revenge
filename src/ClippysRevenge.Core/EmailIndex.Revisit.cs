using Dapper;

namespace ClippysRevenge.Core;

// ai_drafts and ai_revisits members (SPEC 9.1, 23.2, 23.3). Rows of Clippy's own catalogue only: ids, a hash,
// times and counts. Nothing here touches Outlook.
public sealed partial class EmailIndex
{
    public const int MaxDueRevisits = 100;
    public const int MaxThreadInboxRows = 100;

    // Inbox or a subfolder, on rows aliased m. Stored folders are canonical (FolderPaths.IsUnderInbox).
    private const string InboxFilter = "(m.folder = 'Inbox' COLLATE NOCASE OR m.folder LIKE 'Inbox\\%')";

    private const string ThreadSelect =
        "SELECT d.draft_entry_id AS DraftEntryId, d.source_entry_id AS SourceEntryId, d.created AS Created, " +
        "d.fingerprint AS Fingerprint, d.checked AS Checked, d.state AS State, d.attempts AS Attempts, " +
        "d.updated AS Updated, e.conversation_topic AS Topic, e.internet_message_id AS Imid, " +
        "e.event_time AS SourceTime ";

    /// <summary>Inserts the row with state 'open', attempts 0, updated = now. On a draft_entry_id conflict
    /// every column is overwritten (same values as an insert).</summary>
    /// <exception cref="ArgumentException">Validation failed. Nothing is written.</exception>
    public void RecordAiDraft(string draftEntryId, string sourceEntryId, DateTimeOffset created, string fingerprint,
        DateTimeOffset checkedUpTo, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(draftEntryId))
            throw new ArgumentException("AI draft: DraftEntryId is empty.", nameof(draftEntryId));
        if (string.IsNullOrWhiteSpace(sourceEntryId))
            throw new ArgumentException("AI draft: SourceEntryId is empty.", nameof(sourceEntryId));
        if (!DraftFingerprint.IsValid(fingerprint))
            throw new ArgumentException("AI draft: the fingerprint must be 64 lower-case hex characters.", nameof(fingerprint));
        using var conn = OpenConnection();
        conn.Execute(
            """
            INSERT INTO ai_drafts (draft_entry_id, source_entry_id, created, fingerprint, checked, state, attempts, updated)
            VALUES (@d, @s, @created, @fp, @chk, 'open', 0, @now)
            ON CONFLICT(draft_entry_id) DO UPDATE SET
              source_entry_id = excluded.source_entry_id,
              created = excluded.created,
              fingerprint = excluded.fingerprint,
              checked = excluded.checked,
              state = excluded.state,
              attempts = excluded.attempts,
              updated = excluded.updated
            """,
            new
            {
                d = draftEntryId,
                s = sourceEntryId,
                created = created.ToUnixTimeSeconds(),
                fp = fingerprint,
                chk = checkedUpTo.ToUnixTimeSeconds(),
                now = now.ToUnixTimeSeconds(),
            });
    }

    /// <summary>Exact draft_entry_id match. Null when there is no row.</summary>
    /// <exception cref="ArgumentException">draftEntryId null or empty.</exception>
    public AiDraftRecord? GetAiDraft(string draftEntryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(draftEntryId);
        using var conn = OpenConnection();
        var r = conn.QueryFirstOrDefault<AiDraftRow>(
            """
            SELECT draft_entry_id AS DraftEntryId, source_entry_id AS SourceEntryId, created AS Created,
                   fingerprint AS Fingerprint, checked AS Checked, state AS State, attempts AS Attempts, updated AS Updated
            FROM ai_drafts WHERE draft_entry_id = @draftEntryId
            """, new { draftEntryId });
        return r?.ToRecord();
    }

    /// <summary>The newest (created DESC, draft_entry_id DESC) 'open' row whose source row has this
    /// conversation_topic (trimmed, COLLATE NOCASE) and event_time &lt; before. Null for a null/blank topic or no match.
    /// No age limit (the caller checks Created and Checked).</summary>
    public AiDraftThread? OpenAiDraftForThread(string? conversationTopic, DateTimeOffset before)
    {
        var topic = Trimmed(conversationTopic);
        if (topic is null) return null;
        var sql = ThreadSelect +
                  "FROM ai_drafts d JOIN emails e ON e.entry_id = d.source_entry_id " +
                  "WHERE d.state = 'open' AND e.conversation_topic = @topic COLLATE NOCASE AND e.event_time < @before " +
                  "ORDER BY d.created DESC, d.draft_entry_id DESC LIMIT 1";
        using var conn = OpenConnection();
        var r = conn.QueryFirstOrDefault<AiThreadRow>(sql, new { topic, before = before.ToUnixTimeSeconds() });
        return r?.ToThread();
    }

    /// <summary>Newest UpTo first, at most max. Per thread (topic, NOCASE) only the newest open draft with
    /// created &gt;= createdSince counts; it is due when an Inbox-or-under row of the topic is newer than its checked.</summary>
    /// <exception cref="ArgumentOutOfRangeException">max &lt; 1 or &gt; MaxDueRevisits.</exception>
    public IReadOnlyList<DueRevisit> DueRevisits(DateTimeOffset createdSince, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxDueRevisits);
        var sql =
            "SELECT * FROM (" +
            " SELECT t.*, (SELECT MAX(m.event_time) FROM emails m" +
            "   WHERE m.conversation_topic = t.Topic COLLATE NOCASE AND " + InboxFilter +
            "   AND m.event_time > t.Checked) AS UpTo" +
            " FROM (" + ThreadSelect + "," +
            "   ROW_NUMBER() OVER (PARTITION BY e.conversation_topic COLLATE NOCASE" +
            "                      ORDER BY d.created DESC, d.draft_entry_id DESC) AS rn" +
            "   FROM ai_drafts d JOIN emails e ON e.entry_id = d.source_entry_id" +
            "   WHERE d.state = 'open' AND d.created >= @since AND e.conversation_topic IS NOT NULL) t" +
            " WHERE t.rn = 1)" +
            " WHERE UpTo IS NOT NULL" +
            " ORDER BY UpTo DESC, Created DESC, DraftEntryId DESC LIMIT @max";
        using var conn = OpenConnection();
        return conn.Query<AiThreadRow>(sql, new { since = createdSince.ToUnixTimeSeconds(), max })
            .Select(r => new DueRevisit(r.ToThread(), DateTimeOffset.FromUnixTimeSeconds(r.UpTo)))
            .ToList();
    }

    /// <summary>Rows in Inbox or under it whose conversation_topic equals the topic (trimmed, COLLATE NOCASE) and
    /// afterExclusive &lt; event_time &lt;= upToInclusive. Newest first (event_time DESC, id DESC), at most max.
    /// Null/blank topic or afterExclusive &gt;= upToInclusive gives []. Any sender, any addressing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">max &lt; 1 or &gt; MaxThreadInboxRows.</exception>
    public IReadOnlyList<DraftCandidate> ThreadInbox(string? conversationTopic, DateTimeOffset afterExclusive,
        DateTimeOffset upToInclusive, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxThreadInboxRows);
        var topic = Trimmed(conversationTopic);
        if (topic is null || afterExclusive >= upToInclusive) return [];
        var sql =
            "SELECT m.entry_id AS EntryId, m.store_id AS StoreId, m.internet_message_id AS InternetMessageId, " +
            "m.conversation_topic AS ConversationTopic, m.event_time AS EventTime, m.subject AS Subject, " +
            "m.sender_name AS SenderName, m.sender_email AS SenderEmail, m.to_addrs AS ToAddrs, m.cc_addrs AS CcAddrs " +
            "FROM emails m WHERE " + InboxFilter + " AND m.conversation_topic = @topic COLLATE NOCASE " +
            "AND m.event_time > @after AND m.event_time <= @upTo " +
            "ORDER BY m.event_time DESC, m.id DESC LIMIT @max";
        using var conn = OpenConnection();
        return conn.Query<CandidateRow>(sql,
                new { topic, after = afterExclusive.ToUnixTimeSeconds(), upTo = upToInclusive.ToUnixTimeSeconds(), max })
            .Select(r => new DraftCandidate(r.EntryId, r.StoreId, r.InternetMessageId, r.ConversationTopic,
                DateTimeOffset.FromUnixTimeSeconds(r.EventTime), r.Subject, r.SenderName, r.SenderEmail, r.ToAddrs, r.CcAddrs))
            .ToList();
    }

    /// <summary>Changes only a row whose stored state is 'open' (other states are final). Sets state, attempts and
    /// updated = now; checked = MAX(stored checked, checkedUpTo) when checkedUpTo is set, else unchanged.
    /// True when a row was changed.</summary>
    /// <exception cref="ArgumentException">draftEntryId null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">state undefined, or attempts &lt; 0.</exception>
    public bool SetAiDraftState(string draftEntryId, AiDraftState state, DateTimeOffset? checkedUpTo, int attempts,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(draftEntryId);
        var s = AiDraftStates.ToDb(state);
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        using var conn = OpenConnection();
        return conn.Execute(
            """
            UPDATE ai_drafts SET state = @s, attempts = @a, updated = @now,
              checked = CASE WHEN @c IS NULL THEN checked ELSE MAX(checked, @c) END
            WHERE draft_entry_id = @id AND state = 'open'
            """,
            new { s, a = attempts, now = now.ToUnixTimeSeconds(), c = checkedUpTo?.ToUnixTimeSeconds(), id = draftEntryId }) > 0;
    }

    /// <summary>Inserts one ai_revisits row (one per revisit model call).</summary>
    /// <exception cref="ArgumentException">draftEntryId null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">outcome undefined, or modelCalls &lt; 0.</exception>
    public void AddRevisit(string draftEntryId, DateTimeOffset decided, AiRevisitOutcome outcome, int modelCalls)
    {
        ArgumentException.ThrowIfNullOrEmpty(draftEntryId);
        var o = AiRevisitOutcomes.ToDb(outcome);
        ArgumentOutOfRangeException.ThrowIfNegative(modelCalls);
        using var conn = OpenConnection();
        conn.Execute(
            "INSERT INTO ai_revisits (draft_entry_id, decided, outcome, model_calls) VALUES (@id, @decided, @o, @calls)",
            new { id = draftEntryId, decided = decided.ToUnixTimeSeconds(), o, calls = modelCalls });
    }

    private class AiDraftRow
    {
        public string DraftEntryId { get; set; } = "";
        public string SourceEntryId { get; set; } = "";
        public long Created { get; set; }
        public string Fingerprint { get; set; } = "";
        public long Checked { get; set; }
        public string? State { get; set; }
        public long Attempts { get; set; }
        public long Updated { get; set; }

        public AiDraftRecord ToRecord() => new(DraftEntryId, SourceEntryId,
            DateTimeOffset.FromUnixTimeSeconds(Created), Fingerprint, DateTimeOffset.FromUnixTimeSeconds(Checked),
            AiDraftStates.FromDb(State), (int)Attempts, DateTimeOffset.FromUnixTimeSeconds(Updated));
    }

    private sealed class AiThreadRow : AiDraftRow
    {
        public string Topic { get; set; } = "";
        public string? Imid { get; set; }
        public long SourceTime { get; set; }
        public long UpTo { get; set; }

        public AiDraftThread ToThread() => new(ToRecord(), Topic, Imid, DateTimeOffset.FromUnixTimeSeconds(SourceTime));
    }
}
