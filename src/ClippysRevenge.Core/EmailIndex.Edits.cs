using Dapper;

namespace ClippysRevenge.Core;

/// <summary>State of an ai_draft_texts row. Database values: "pending", "matched", "superseded", "expired".</summary>
public enum AiDraftTextState { Pending, Matched, Superseded, Expired }

/// <summary>One ai_draft_texts row (SPEC 27.2). Mailbox: "" own, else the shared mailbox's SMTP.</summary>
public sealed record AiDraftText(string DraftEntryId, string Mailbox, string SourceImid, DateTimeOffset Created,
    string Text, AiDraftTextState State, DateTimeOffset Updated);

/// <summary>A Sent Items row that began as a Clippy AI draft and has a pending draft text to match (SPEC 27.3).
/// Body is cut to MaxThreadBodyChars.</summary>
public sealed record SentAiReply(string EntryId, DateTimeOffset EventTime, string InReplyTo, string? Body);

/// <summary>One ai_edits row (SPEC 27.2, 27.4). Texts are both null (sent as drafted, or aged out) or both set.</summary>
public sealed record AiEdit(string SentEntryId, DateTimeOffset Sent, double Similarity, int DraftChars, int SentChars,
    string? DraftText, string? SentText);

/// <summary>Input of the style prompt's my_edits block (SPEC 27.6). Pairs: rows with both texts, newest first.</summary>
public sealed record AiEditsForStyle(IReadOnlyList<AiEdit> Pairs, int SentUnchanged);

// Learning from edits (SPEC 9.1, 27.2-27.5). Index rows only: nothing here touches Outlook. The texts are the
// user's own redacted mail; nothing in this file logs.
public sealed partial class EmailIndex
{
    public const int MaxEditTextChars = 1_500;
    public const int MaxEditPairs = 20;                                     // rows with texts (SPEC 27.5)
    public const int MaxSentRepliesToMatch = 500;
    public static readonly TimeSpan DraftTextRetention = TimeSpan.FromDays(30);   // pending → expired, then deleted
    public static readonly TimeSpan EditRetention = TimeSpan.FromDays(90);

    private static AiDraftTextState StateFromDb(string? s) => s switch
    {
        "matched" => AiDraftTextState.Matched,
        "superseded" => AiDraftTextState.Superseded,
        "expired" => AiDraftTextState.Expired,
        _ => AiDraftTextState.Pending,
    };

    /// <summary>Insert, or on a draft_entry_id conflict overwrite every column; state 'pending', updated = created.
    /// sourceImid is stored trimmed.</summary>
    /// <exception cref="ArgumentNullException">mailbox or text is null.</exception>
    /// <exception cref="ArgumentException">draftEntryId or sourceImid null/blank, or text longer than
    /// MaxEditTextChars. Nothing is written.</exception>
    public void RecordAiDraftText(string draftEntryId, string mailbox, string sourceImid, DateTimeOffset created, string text)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(draftEntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImid);
        if (text.Length > MaxEditTextChars)
            throw new ArgumentException($"A draft text is at most {MaxEditTextChars} characters.", nameof(text));
        using var conn = OpenConnection();
        conn.Execute(
            """
            INSERT INTO ai_draft_texts (draft_entry_id, mailbox, source_imid, created, text, state, updated)
            VALUES (@id, @mailbox, @imid, @created, @text, 'pending', @created)
            ON CONFLICT(draft_entry_id) DO UPDATE SET
              mailbox = excluded.mailbox,
              source_imid = excluded.source_imid,
              created = excluded.created,
              text = excluded.text,
              state = excluded.state,
              updated = excluded.updated
            """,
            new { id = draftEntryId, mailbox, imid = sourceImid.Trim(), created = created.ToUnixTimeSeconds(), text });
    }

    /// <summary>Exact draft_entry_id match; null when there is no row.</summary>
    /// <exception cref="ArgumentException">draftEntryId null or empty.</exception>
    public AiDraftText? GetAiDraftText(string draftEntryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(draftEntryId);
        using var conn = OpenConnection();
        var r = conn.QueryFirstOrDefault<DraftTextRow>(
            """
            SELECT draft_entry_id AS DraftEntryId, mailbox AS Mailbox, source_imid AS SourceImid, created AS Created,
                   text AS Text, state AS State, updated AS Updated
            FROM ai_draft_texts WHERE draft_entry_id = @draftEntryId
            """, new { draftEntryId });
        return r is null ? null : ToDraftText(r);
    }

    /// <summary>SPEC 27.3 sent rows: Sent Items or under, clippy_marker = 'ai', in_reply_to not null/blank,
    /// event_time &gt;= since, no ai_edits row for the entry id, and at least one pending ai_draft_texts row with
    /// source_imid = in_reply_to (ordinal) and created &lt;= event_time. Newest first (event_time DESC, id DESC).</summary>
    /// <exception cref="ArgumentOutOfRangeException">max &lt; 1 or &gt; MaxSentRepliesToMatch.</exception>
    public IReadOnlyList<SentAiReply> SentAiRepliesToMatch(DateTimeOffset since, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, MaxSentRepliesToMatch);
        var sql = "SELECT e.entry_id AS EntryId, e.event_time AS EventTime, e.in_reply_to AS InReplyTo, " +
                  "substr(e.body, 1, " + MaxThreadBodyChars + ") AS Body FROM emails e WHERE " + SentItemsFilter +
                  " AND e.clippy_marker = 'ai' AND e.in_reply_to IS NOT NULL AND trim(e.in_reply_to) <> '' " +
                  "AND e.event_time >= @since " +
                  "AND NOT EXISTS (SELECT 1 FROM ai_edits x WHERE x.sent_entry_id = e.entry_id) " +
                  "AND EXISTS (SELECT 1 FROM ai_draft_texts t WHERE t.state = 'pending' " +
                  "AND t.source_imid = e.in_reply_to AND t.created <= e.event_time) " +
                  "ORDER BY e.event_time DESC, e.id DESC LIMIT @max";
        using var conn = OpenConnection();
        return conn.Query<SentReplyRow>(sql, new { since = since.ToUnixTimeSeconds(), max })
            .Select(r => new SentAiReply(r.EntryId, DateTimeOffset.FromUnixTimeSeconds(r.EventTime), r.InReplyTo, r.Body))
            .ToList();
    }

    /// <summary>Pending rows with source_imid = sourceImid (trimmed, ordinal) and created &lt;= createdUpTo, newest
    /// first (created DESC, draft_entry_id DESC), at most 20. Null/blank imid gives [].</summary>
    public IReadOnlyList<AiDraftText> PendingDraftTexts(string? sourceImid, DateTimeOffset createdUpTo)
    {
        var imid = Trimmed(sourceImid);
        if (imid is null) return [];
        using var conn = OpenConnection();
        return conn.Query<DraftTextRow>(
            """
            SELECT draft_entry_id AS DraftEntryId, mailbox AS Mailbox, source_imid AS SourceImid, created AS Created,
                   text AS Text, state AS State, updated AS Updated
            FROM ai_draft_texts
            WHERE state = 'pending' AND source_imid = @imid AND created <= @upTo
            ORDER BY created DESC, draft_entry_id DESC LIMIT 20
            """, new { imid, upTo = createdUpTo.ToUnixTimeSeconds() })
            .Select(ToDraftText)
            .ToList();
    }

    /// <summary>SPEC 27.3 steps 3-4 and 27.5 in one transaction. False (and nothing changed) when the matched row is
    /// no longer pending or an ai_edits row for edit.SentEntryId exists. The matched and the older pending rows of the
    /// same source lose their text; at most <see cref="MaxEditPairs"/> ai_edits rows keep theirs.</summary>
    /// <exception cref="ArgumentNullException">edit is null.</exception>
    /// <exception cref="ArgumentException">matchedDraftEntryId/sourceImid/edit.SentEntryId blank; exactly one text
    /// null; a text longer than MaxEditTextChars; negative chars.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Similarity NaN, infinite, &lt; 0 or &gt; 1.</exception>
    public bool StoreEdit(string matchedDraftEntryId, string sourceImid, AiEdit edit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (string.IsNullOrWhiteSpace(matchedDraftEntryId)) throw new ArgumentException("The matched draft id is empty.", nameof(matchedDraftEntryId));
        if (string.IsNullOrWhiteSpace(sourceImid)) throw new ArgumentException("The source message id is empty.", nameof(sourceImid));
        if (string.IsNullOrWhiteSpace(edit.SentEntryId)) throw new ArgumentException("The sent entry id is empty.", nameof(edit));
        if (double.IsNaN(edit.Similarity) || double.IsInfinity(edit.Similarity) || edit.Similarity < 0 || edit.Similarity > 1)
            throw new ArgumentOutOfRangeException(nameof(edit), "Similarity must be between 0 and 1.");
        if ((edit.DraftText is null) != (edit.SentText is null))
            throw new ArgumentException("Both texts are null or both are set.", nameof(edit));
        if (edit.DraftText is { Length: > MaxEditTextChars } || edit.SentText is { Length: > MaxEditTextChars })
            throw new ArgumentException($"An edit text is at most {MaxEditTextChars} characters.", nameof(edit));
        if (edit.DraftChars < 0 || edit.SentChars < 0)
            throw new ArgumentException("Character counts can't be negative.", nameof(edit));

        var p = new
        {
            m = matchedDraftEntryId,
            imid = sourceImid.Trim(),
            now = now.ToUnixTimeSeconds(),
            sent = edit.Sent.ToUnixTimeSeconds(),
            sentId = edit.SentEntryId,
            sim = edit.Similarity,
            dc = edit.DraftChars,
            sc = edit.SentChars,
            dt = edit.DraftText,
            st = edit.SentText,
            keep = MaxEditPairs,
        };
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        if (conn.Execute(
                "UPDATE ai_draft_texts SET state = 'matched', text = '', updated = @now WHERE draft_entry_id = @m AND state = 'pending'",
                p, tx) == 0)
        {
            tx.Rollback();
            return false;
        }
        conn.Execute(
            "UPDATE ai_draft_texts SET state = 'superseded', text = '', updated = @now " +
            "WHERE state = 'pending' AND source_imid = @imid AND created <= @sent", p, tx);
        if (conn.Execute(
                "INSERT INTO ai_edits (sent_entry_id, sent, similarity, draft_chars, sent_chars, draft_text, sent_text) " +
                "VALUES (@sentId, @sent, @sim, @dc, @sc, @dt, @st) ON CONFLICT(sent_entry_id) DO NOTHING", p, tx) == 0)
        {
            tx.Rollback();
            return false;
        }
        if (edit.DraftText is not null)
            conn.Execute(
                "UPDATE ai_edits SET draft_text = NULL, sent_text = NULL WHERE draft_text IS NOT NULL AND id NOT IN " +
                "(SELECT id FROM ai_edits WHERE draft_text IS NOT NULL ORDER BY sent DESC, id DESC LIMIT @keep)", p, tx);
        tx.Commit();
        return true;
    }

    /// <summary>Newest first (sent DESC, id DESC), at most max. For tests and M20.2.</summary>
    /// <exception cref="ArgumentOutOfRangeException">max &lt; 1 or &gt; 1000.</exception>
    public IReadOnlyList<AiEdit> AiEdits(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(max, 1000);
        using var conn = OpenConnection();
        return conn.Query<EditRow>(
            """
            SELECT sent_entry_id AS SentEntryId, sent AS Sent, similarity AS Similarity, draft_chars AS DraftChars,
                   sent_chars AS SentChars, draft_text AS DraftText, sent_text AS SentText
            FROM ai_edits ORDER BY sent DESC, id DESC LIMIT @max
            """, new { max })
            .Select(r => new AiEdit(r.SentEntryId, DateTimeOffset.FromUnixTimeSeconds(r.Sent), r.Similarity,
                (int)r.DraftChars, (int)r.SentChars, r.DraftText, r.SentText))
            .ToList();
    }

    /// <summary>SPEC 27.6: Pairs = ai_edits rows with draft_text and sent_text not null and sent &gt;= since,
    /// ORDER BY sent DESC, id DESC LIMIT MaxEditPairs. SentUnchanged = count(*) of rows with
    /// similarity &gt;= EditDiff.UnchangedThreshold and sent &gt;= since.</summary>
    public AiEditsForStyle EditsForStyle(DateTimeOffset since)
    {
        using var conn = OpenConnection();
        var s = since.ToUnixTimeSeconds();
        var pairs = conn.Query<EditRow>(
            """
            SELECT sent_entry_id AS SentEntryId, sent AS Sent, similarity AS Similarity, draft_chars AS DraftChars,
                   sent_chars AS SentChars, draft_text AS DraftText, sent_text AS SentText
            FROM ai_edits WHERE draft_text IS NOT NULL AND sent_text IS NOT NULL AND sent >= @since
            ORDER BY sent DESC, id DESC LIMIT @keep
            """, new { since = s, keep = MaxEditPairs })
            .Select(r => new AiEdit(r.SentEntryId, DateTimeOffset.FromUnixTimeSeconds(r.Sent), r.Similarity,
                (int)r.DraftChars, (int)r.SentChars, r.DraftText, r.SentText))
            .ToList();
        var unchanged = conn.ExecuteScalar<long>(
            "SELECT count(*) FROM ai_edits WHERE similarity >= @t AND sent >= @since",
            new { t = EditDiff.UnchangedThreshold, since = s });
        return new AiEditsForStyle(pairs, (int)Math.Min(unchanged, int.MaxValue));
    }

    private static AiDraftText ToDraftText(DraftTextRow r) =>
        new(r.DraftEntryId, r.Mailbox, r.SourceImid, DateTimeOffset.FromUnixTimeSeconds(r.Created), r.Text,
            StateFromDb(r.State), DateTimeOffset.FromUnixTimeSeconds(r.Updated));

    private sealed class DraftTextRow
    {
        public string DraftEntryId { get; set; } = "";
        public string Mailbox { get; set; } = "";
        public string SourceImid { get; set; } = "";
        public long Created { get; set; }
        public string Text { get; set; } = "";
        public string? State { get; set; }
        public long Updated { get; set; }
    }

    private sealed class SentReplyRow
    {
        public string EntryId { get; set; } = "";
        public long EventTime { get; set; }
        public string InReplyTo { get; set; } = "";
        public string? Body { get; set; }
    }

    private sealed class EditRow
    {
        public string SentEntryId { get; set; } = "";
        public long Sent { get; set; }
        public double Similarity { get; set; }
        public long DraftChars { get; set; }
        public long SentChars { get; set; }
        public string? DraftText { get; set; }
        public string? SentText { get; set; }
    }
}
