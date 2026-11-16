using Dapper;

namespace ClippysRevenge.Core;

// Related earlier mail for drafting prompts (SPEC 9.2, 25.3). Read-only: it never writes and never logs.
public sealed partial class EmailIndex
{
    private static readonly string RelatedColumns =
        "e.entry_id AS EntryId, e.internet_message_id AS InternetMessageId, e.conversation_topic AS ConversationTopic, " +
        "e.folder AS Folder, e.event_time AS EventTime, e.sender_name AS SenderName, e.sender_email AS SenderEmail, " +
        "e.to_addrs AS ToAddrs, e.cc_addrs AS CcAddrs, e.subject AS Subject, " +
        "substr(e.body, 1, " + RelatedMail.BodyFetchChars + ") AS Body";

    private const string RelatedFilter =
        "e.event_time >= @since AND e.event_time <= @until " +
        "AND e.entry_id <> @anchor " +
        "AND (@imid IS NULL OR e.internet_message_id IS NULL OR e.internet_message_id <> @imid) " +
        "AND (@imid IS NULL OR e.in_reply_to IS NULL OR e.in_reply_to <> @imid) " +
        "AND (@topic IS NULL OR e.conversation_topic IS NULL OR e.conversation_topic <> @topic COLLATE NOCASE) " +
        "AND NOT (e.folder = 'Drafts' COLLATE NOCASE OR e.folder LIKE 'Drafts\\%') " +
        "AND NOT (e.folder = 'Deleted Items' COLLATE NOCASE OR e.folder LIKE 'Deleted Items\\%')";

    /// <summary>SPEC 25.3 candidates: the terms query and the people query, one connection.
    /// Read-only. Rows are RelatedRow with Body = substr(body, 1, RelatedMail.BodyFetchChars).</summary>
    /// <exception cref="ArgumentNullException">query, query.AnchorEntryId or query.Terms is null.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Database errors (the worker catches them).</exception>
    public RelatedHits RelatedCandidates(RelatedQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.AnchorEntryId);
        ArgumentNullException.ThrowIfNull(query.Terms);

        var common = new Dictionary<string, object?>
        {
            ["since"] = query.Since.ToUnixTimeSeconds(),
            ["until"] = query.Until.ToUnixTimeSeconds(),
            ["anchor"] = query.AnchorEntryId,
            ["imid"] = Trimmed(query.AnchorImid),
            ["topic"] = Trimmed(query.AnchorTopic),
        };

        string? match = null;
        var usable = query.Terms.Where(t => !string.IsNullOrEmpty(t) && t.All(char.IsLetterOrDigit)
            && !string.Equals(t, "OR", StringComparison.Ordinal)).ToList();
        if (usable.Count > 0)
        {
            var inner = FtsQuery.Build(string.Join(" OR ", usable));
            if (inner is not null) match = "{subject body} : (" + inner + ")";
        }
        var sender = Trimmed(query.SenderEmail);
        if (match is null && sender is null) return RelatedHits.None;

        using var conn = OpenConnection();

        IReadOnlyList<RelatedRow> termHits = [];
        if (match is not null)
        {
            var p = new DynamicParameters(common);
            p.Add("q", match);
            var sql = "SELECT " + RelatedColumns + " FROM emails_fts JOIN emails e ON e.id = emails_fts.rowid " +
                      "WHERE emails_fts MATCH @q AND " + RelatedFilter +
                      " ORDER BY bm25(emails_fts), e.event_time DESC, e.id DESC LIMIT " + RelatedMail.MaxPerQuery;
            termHits = conn.Query<RelatedDbRow>(sql, p).Select(r => r.ToRow()).ToList();
        }

        IReadOnlyList<RelatedRow> peopleHits = [];
        if (sender is not null)
        {
            var p = new DynamicParameters(common);
            p.Add("s", sender);
            p.Add("sl", sender.ToLowerInvariant());
            var sql = "SELECT " + RelatedColumns + " FROM emails e WHERE " + RelatedFilter +
                      " AND (e.sender_email = @s COLLATE NOCASE" +
                      " OR ((e.folder = 'Sent Items' COLLATE NOCASE OR e.folder LIKE 'Sent Items\\%')" +
                      " AND (instr(lower(COALESCE(e.to_addrs, '')), @sl) > 0 OR instr(lower(COALESCE(e.cc_addrs, '')), @sl) > 0)))" +
                      " ORDER BY e.event_time DESC, e.id DESC LIMIT " + RelatedMail.MaxPerQuery;
            peopleHits = conn.Query<RelatedDbRow>(sql, p)
                .Where(r => string.Equals(r.SenderEmail?.Trim(), sender, StringComparison.OrdinalIgnoreCase)
                    || DraftRules.Addresses(r.ToAddrs).Contains(sender, StringComparer.OrdinalIgnoreCase)
                    || DraftRules.Addresses(r.CcAddrs).Contains(sender, StringComparer.OrdinalIgnoreCase))
                .Select(r => r.ToRow()).ToList();
        }

        return new RelatedHits(termHits, peopleHits);
    }

    private sealed class RelatedDbRow
    {
        public string EntryId { get; set; } = "";
        public string? InternetMessageId { get; set; }
        public string? ConversationTopic { get; set; }
        public string Folder { get; set; } = "";
        public long EventTime { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmail { get; set; }
        public string? ToAddrs { get; set; }
        public string? CcAddrs { get; set; }
        public string? Subject { get; set; }
        public string? Body { get; set; }

        public RelatedRow ToRow() => new(EntryId, InternetMessageId, ConversationTopic, Folder,
            DateTimeOffset.FromUnixTimeSeconds(EventTime), SenderName, SenderEmail, ToAddrs, CcAddrs, Subject, Body);
    }
}
