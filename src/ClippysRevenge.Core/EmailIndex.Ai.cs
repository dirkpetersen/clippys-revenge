using System.Text.RegularExpressions;
using Dapper;

namespace ClippysRevenge.Core;

// Thread metadata and ai_decisions members (SPEC 9.1, 9.2, 19). Index rows only: nothing here touches Outlook.
public sealed partial class EmailIndex
{
    private const string SentItemsFilter = "(e.folder = 'Sent Items' COLLATE NOCASE OR e.folder LIKE 'Sent Items\\%')";

    private static readonly Regex RuleId = new("^[a-z_]+\\z", RegexOptions.CultureInvariant);

    /// <summary>Deletes ai_decisions rows with decided older than now minus <see cref="DecisionRetention"/>. Returns the count. No VACUUM.</summary>
    public int PurgeDecisions(DateTimeOffset now)
    {
        using var conn = OpenConnection();
        return conn.Execute("DELETE FROM ai_decisions WHERE decided < @t",
            new { t = (now - DecisionRetention).ToUnixTimeSeconds() });
    }

    /// <summary>
    /// Updates only in_reply_to, conversation_topic and clippy_marker of existing rows, in one transaction.
    /// A null value never clears a stored one. Matches entry_id exactly, then upper-case. Never inserts or deletes.
    /// Returns the number of rows matched.
    /// </summary>
    public int SetThreadMeta(IEnumerable<(string EntryId, string? InReplyTo, string? ConversationTopic, string? ClippyMarker)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.Where(r => !string.IsNullOrWhiteSpace(r.EntryId)).ToList();
        if (list.Count == 0) return 0;
        const string sql = """
            UPDATE emails SET
              in_reply_to = COALESCE(@irt, in_reply_to),
              conversation_topic = COALESCE(@topic, conversation_topic),
              clippy_marker = COALESCE(@marker, clippy_marker)
            WHERE entry_id = @e
            """;
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var matched = 0;
        foreach (var r in list)
        {
            var m = MailThreadMeta.FromValues(r.InReplyTo, r.ConversationTopic, r.ClippyMarker);
            var n = conn.Execute(sql, new { irt = m.InReplyTo, topic = m.ConversationTopic, marker = m.ClippyMarker, e = r.EntryId }, tx);
            if (n == 0)
            {
                var upper = r.EntryId.ToUpperInvariant();
                if (!string.Equals(upper, r.EntryId, StringComparison.Ordinal))
                    n = conn.Execute(sql, new { irt = m.InReplyTo, topic = m.ConversationTopic, marker = m.ClippyMarker, e = upper }, tx);
            }
            matched += n > 0 ? 1 : 0;
        }
        tx.Commit();
        return matched;
    }

    /// <summary>
    /// The newest max rows whose conversation_topic equals conversationTopic (trimmed; COLLATE NOCASE), with
    /// event_time &lt;= before, any folder, newest first. excludeEntryId (exact) is left out.
    /// Null or blank topic gives []. Body is cut to <see cref="MaxThreadBodyChars"/>.
    /// </summary>
    public IReadOnlyList<ThreadMessage> Thread(string? conversationTopic, DateTimeOffset before, int max,
        string? excludeEntryId = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, 100);
        var topic = Trimmed(conversationTopic);
        if (topic is null) return [];
        var sql = "SELECT e.entry_id AS EntryId, e.folder AS Folder, e.event_time AS EventTime, e.sender_name AS SenderName, " +
                  "e.sender_email AS SenderEmail, e.to_addrs AS ToAddrs, e.cc_addrs AS CcAddrs, e.subject AS Subject, " +
                  "e.attachment_names AS AttachmentNames, substr(e.body, 1, " + MaxThreadBodyChars + ") AS Body " +
                  "FROM emails e WHERE e.conversation_topic = @t COLLATE NOCASE AND e.event_time <= @before " +
                  (excludeEntryId is null ? "" : "AND e.entry_id <> @x ") +
                  "ORDER BY e.event_time DESC, e.id DESC LIMIT @max";
        using var conn = OpenConnection();
        return conn.Query<ThreadRow>(sql, new { t = topic, before = before.ToUnixTimeSeconds(), x = excludeEntryId, max })
            .Select(r => new ThreadMessage(
                r.EntryId, r.Folder, DateTimeOffset.FromUnixTimeSeconds(r.EventTime), r.SenderName, r.SenderEmail,
                r.ToAddrs, r.CcAddrs, r.Subject,
                (r.AttachmentNames ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries),
                r.Body))
            .ToList();
    }

    /// <summary>
    /// True when a row in Sent Items (or under it) has in_reply_to = sourceImid, or conversation_topic =
    /// conversationTopic (NOCASE) and event_time &gt; after. A null/blank half is skipped.
    /// </summary>
    public bool RepliedTo(string? sourceImid, string? conversationTopic, DateTimeOffset after)
    {
        var imid = Trimmed(sourceImid);
        var topic = Trimmed(conversationTopic);
        var halves = new List<string>();
        if (imid is not null) halves.Add("e.in_reply_to = @imid");
        if (topic is not null) halves.Add("(e.conversation_topic = @topic COLLATE NOCASE AND e.event_time > @after)");
        if (halves.Count == 0) return false;
        var sql = "SELECT EXISTS(SELECT 1 FROM emails e WHERE " + SentItemsFilter + " AND (" + string.Join(" OR ", halves) + "))";
        using var conn = OpenConnection();
        return conn.ExecuteScalar<long>(sql, new { imid, topic, after = after.ToUnixTimeSeconds() }) != 0;
    }

    /// <summary>
    /// Sent Items rows (or under it) with from &lt;= event_time &lt;= to, no ClippyDraft marker, and not
    /// AI-originated (SPEC 19.4). Newest first, at most max. Body cut to <see cref="MaxThreadBodyChars"/>.
    /// </summary>
    public IReadOnlyList<SentMail> SentForStyle(DateTimeOffset from, DateTimeOffset to, int max = 5_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, 10_000);
        if (from > to) return [];
        var sql = "SELECT e.entry_id AS EntryId, e.internet_message_id AS InternetMessageId, e.event_time AS EventTime, " +
                  "e.subject AS Subject, e.to_addrs AS ToAddrs, e.cc_addrs AS CcAddrs, " +
                  "substr(e.body, 1, " + MaxThreadBodyChars + ") AS Body FROM emails e WHERE " + SentItemsFilter +
                  " AND e.event_time >= @from AND e.event_time <= @to AND e.clippy_marker IS NULL " +
                  "AND NOT EXISTS (SELECT 1 FROM ai_decisions d WHERE d.outcome = 'drafted' AND d.source_imid IS NOT NULL " +
                  "AND d.source_imid = e.in_reply_to AND e.event_time > COALESCE(d.draft_created, 0)) " +
                  "ORDER BY e.event_time DESC, e.id DESC LIMIT @max";
        using var conn = OpenConnection();
        return conn.Query<SentRow>(sql, new { from = from.ToUnixTimeSeconds(), to = to.ToUnixTimeSeconds(), max })
            .Select(r => new SentMail(r.EntryId, r.InternetMessageId, DateTimeOffset.FromUnixTimeSeconds(r.EventTime),
                r.Subject, r.ToAddrs, r.CcAddrs, r.Body))
            .ToList();
    }

    /// <summary>
    /// Inbox rows (not subfolders) with event_time &gt;= since and either no ai_decisions row or a 'failed' row
    /// with attempts &lt; <see cref="MaxDecisionAttempts"/>. Newest first, at most max.
    /// </summary>
    public IReadOnlyList<DraftCandidate> DraftCandidates(DateTimeOffset since, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, 500);
        const string sql = """
            SELECT e.entry_id AS EntryId, e.store_id AS StoreId, e.internet_message_id AS InternetMessageId,
                   e.conversation_topic AS ConversationTopic, e.event_time AS EventTime, e.subject AS Subject,
                   e.sender_name AS SenderName, e.sender_email AS SenderEmail, e.to_addrs AS ToAddrs, e.cc_addrs AS CcAddrs
            FROM emails e LEFT JOIN ai_decisions d ON d.source_entry_id = e.entry_id
            WHERE e.folder = 'Inbox' COLLATE NOCASE AND e.event_time >= @since
              AND (d.source_entry_id IS NULL OR (d.outcome = 'failed' AND d.attempts < @maxAttempts))
            ORDER BY e.event_time DESC, e.id DESC LIMIT @max
            """;
        using var conn = OpenConnection();
        return conn.Query<CandidateRow>(sql, new { since = since.ToUnixTimeSeconds(), maxAttempts = MaxDecisionAttempts, max })
            .Select(r => new DraftCandidate(r.EntryId, r.StoreId, r.InternetMessageId, r.ConversationTopic,
                DateTimeOffset.FromUnixTimeSeconds(r.EventTime), r.Subject, r.SenderName, r.SenderEmail, r.ToAddrs, r.CcAddrs))
            .ToList();
    }

    /// <exception cref="ArgumentException">sourceEntryId null or empty.</exception>
    public AiDecision? GetDecision(string sourceEntryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceEntryId);
        using var conn = OpenConnection();
        // conversation_topic is never read (SPEC 19.11).
        var r = conn.QueryFirstOrDefault<DecisionRow>(
            """
            SELECT source_entry_id AS SourceEntryId, source_imid AS SourceImid, decided AS Decided, outcome AS Outcome,
                   rule AS Rule, model_calls AS ModelCalls, attempts AS Attempts, confidence AS Confidence,
                   draft_entry_id AS DraftEntryId, draft_created AS DraftCreated
            FROM ai_decisions WHERE source_entry_id = @sourceEntryId
            """, new { sourceEntryId });
        if (r is null) return null;
        return new AiDecision(
            r.SourceEntryId, r.SourceImid, null, DateTimeOffset.FromUnixTimeSeconds(r.Decided),
            AiOutcomes.FromDb(r.Outcome), r.Rule, (int)r.ModelCalls, (int)r.Attempts, r.Confidence, r.DraftEntryId,
            r.DraftCreated is { } dc ? DateTimeOffset.FromUnixTimeSeconds(dc) : null);
    }

    /// <summary>Insert or replace by source_entry_id. conversation_topic is always stored as NULL (SPEC 19.11).</summary>
    /// <exception cref="ArgumentNullException">decision is null.</exception>
    /// <exception cref="ArgumentException">Invalid decision. Nothing is written.</exception>
    public void UpsertDecision(AiDecision decision)
    {
        ValidateDecision(decision);
        using var conn = OpenConnection();
        conn.Execute(
            """
            INSERT INTO ai_decisions (source_entry_id, source_imid, conversation_topic, decided, outcome, rule,
                                      model_calls, attempts, confidence, draft_entry_id, draft_created)
            VALUES (@id, @imid, NULL, @decided, @outcome, @rule, @calls, @attempts, @confidence, @draftId, @draftCreated)
            ON CONFLICT(source_entry_id) DO UPDATE SET
              source_imid = excluded.source_imid,
              conversation_topic = excluded.conversation_topic,
              decided = excluded.decided,
              outcome = excluded.outcome,
              rule = excluded.rule,
              model_calls = excluded.model_calls,
              attempts = excluded.attempts,
              confidence = excluded.confidence,
              draft_entry_id = excluded.draft_entry_id,
              draft_created = excluded.draft_created
            """,
            new
            {
                id = decision.SourceEntryId,
                imid = decision.SourceImid,
                decided = decision.Decided.ToUnixTimeSeconds(),
                outcome = AiOutcomes.ToDb(decision.Outcome),
                rule = decision.Rule,
                calls = decision.ModelCalls,
                attempts = decision.Attempts,
                confidence = decision.Confidence,
                draftId = decision.DraftEntryId,
                draftCreated = decision.DraftCreated?.ToUnixTimeSeconds(),
            });
    }

    /// <summary>COALESCE(SUM(model_calls), 0) over rows with decided &gt;= sinceUtc.</summary>
    public int ModelCallsSince(DateTimeOffset sinceUtc)
    {
        using var conn = OpenConnection();
        return (int)conn.ExecuteScalar<long>(
            "SELECT COALESCE(SUM(model_calls), 0) FROM ai_decisions WHERE decided >= @s",
            new { s = sinceUtc.ToUnixTimeSeconds() });
    }

    private static void ValidateDecision(AiDecision? d)
    {
        ArgumentNullException.ThrowIfNull(d, "decision");
        static ArgumentException Bad(string msg) => new("AI decision: " + msg, "decision");
        if (string.IsNullOrWhiteSpace(d.SourceEntryId)) throw Bad("SourceEntryId is empty.");
        if (!Enum.IsDefined(d.Outcome)) throw Bad("unknown outcome.");
        if (d.Outcome == AiOutcome.Skipped && string.IsNullOrWhiteSpace(d.Rule))
            throw Bad("a skipped outcome needs a rule id.");
        if (d.Outcome != AiOutcome.Skipped && d.Rule is not null)
            throw Bad("only a skipped outcome has a rule id.");
        if (d.Rule is not null && (d.Rule.Length > 32 || !RuleId.IsMatch(d.Rule)))
            throw Bad("rule ids are lower-case letters and '_'.");
        if (d.ModelCalls < 0 || d.Attempts < 0) throw Bad("counts can't be negative.");
        if (d.Confidence is { } c && (double.IsNaN(c) || double.IsInfinity(c) || c < 0 || c > 1))
            throw Bad("confidence must be between 0 and 1.");
    }

    private sealed class ThreadRow
    {
        public string EntryId { get; set; } = "";
        public string Folder { get; set; } = "";
        public long EventTime { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmail { get; set; }
        public string? ToAddrs { get; set; }
        public string? CcAddrs { get; set; }
        public string? Subject { get; set; }
        public string? AttachmentNames { get; set; }
        public string? Body { get; set; }
    }

    private sealed class SentRow
    {
        public string EntryId { get; set; } = "";
        public string? InternetMessageId { get; set; }
        public long EventTime { get; set; }
        public string? Subject { get; set; }
        public string? ToAddrs { get; set; }
        public string? CcAddrs { get; set; }
        public string? Body { get; set; }
    }

    private sealed class CandidateRow
    {
        public string EntryId { get; set; } = "";
        public string? StoreId { get; set; }
        public string? InternetMessageId { get; set; }
        public string? ConversationTopic { get; set; }
        public long EventTime { get; set; }
        public string? Subject { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmail { get; set; }
        public string? ToAddrs { get; set; }
        public string? CcAddrs { get; set; }
    }

    private sealed class DecisionRow
    {
        public string SourceEntryId { get; set; } = "";
        public string? SourceImid { get; set; }
        public long Decided { get; set; }
        public string? Outcome { get; set; }
        public string? Rule { get; set; }
        public long ModelCalls { get; set; }
        public long Attempts { get; set; }
        public double? Confidence { get; set; }
        public string? DraftEntryId { get; set; }
        public long? DraftCreated { get; set; }
    }
}
