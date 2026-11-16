using Dapper;

namespace ClippysRevenge.Core;

/// <summary>One earlier message of the anchor's thread (SPEC 26.8). Body: the index plain-text body cut to
/// EmailIndex.MaxThreadBodyChars. Attachments: every attachments row in position order, inline ones included
/// (IsInline set), Size null when unknown (never 0 for unknown).</summary>
public sealed record ThreadAttachmentSource(string EntryId, DateTimeOffset EventTime, string? SenderName,
    string? SenderEmail, string? Body, IReadOnlyList<AttachmentInfo> Attachments);

// Read-only thread query for attachment reading (SPEC 26.8). Index rows only.
public sealed partial class EmailIndex
{
    public const int MaxThreadAttachmentMessages = 50;   // SPEC 26.1 MaxThreadMessagesScanned

    private sealed class ThreadAttachmentMailRow
    {
        public long Id { get; set; }
        public string EntryId { get; set; } = "";
        public long EventTime { get; set; }
        public string? SenderName { get; set; }
        public string? SenderEmail { get; set; }
        public string? Body { get; set; }
    }

    private sealed class ThreadAttachmentRow
    {
        public long EmailId { get; set; }
        public long Position { get; set; }
        public string? FileName { get; set; }
        public long? Size { get; set; }
        public long? IsInline { get; set; }
    }

    /// <summary>Read-only. Rows with conversation_topic = topic (trimmed, COLLATE NOCASE), event_time &lt;= upTo,
    /// any folder, entry_id &lt;&gt; excludeEntryId (exact), newest first (event_time DESC, id DESC), at most
    /// maxMessages. Rows without attachments are included (their bodies may hold links).
    /// Null or blank topic gives [].</summary>
    /// <exception cref="ArgumentOutOfRangeException">maxMessages &lt; 1 or &gt; 100.</exception>
    /// <exception cref="ArgumentNullException">excludeEntryId is null.</exception>
    public IReadOnlyList<ThreadAttachmentSource> ThreadAttachments(string? topic, DateTimeOffset upTo,
        string excludeEntryId, int maxMessages = MaxThreadAttachmentMessages)
    {
        ArgumentNullException.ThrowIfNull(excludeEntryId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxMessages, 100);
        var t = Trimmed(topic);
        if (t is null) return [];

        var sql = "SELECT e.id AS Id, e.entry_id AS EntryId, e.event_time AS EventTime, e.sender_name AS SenderName, " +
                  "e.sender_email AS SenderEmail, substr(e.body, 1, " + MaxThreadBodyChars + ") AS Body " +
                  "FROM emails e WHERE e.conversation_topic = @t COLLATE NOCASE AND e.event_time <= @upTo " +
                  "AND e.entry_id <> @x ORDER BY e.event_time DESC, e.id DESC LIMIT @max";
        using var conn = OpenConnection();
        var mails = conn.Query<ThreadAttachmentMailRow>(sql,
            new { t, upTo = upTo.ToUnixTimeSeconds(), x = excludeEntryId, max = maxMessages }).ToList();
        if (mails.Count == 0) return [];

        var ids = mails.Select(m => m.Id).ToArray();
        var byMail = conn.Query<ThreadAttachmentRow>(
                "SELECT email_id AS EmailId, position AS Position, filename AS FileName, size AS Size, " +
                "is_inline AS IsInline FROM attachments WHERE email_id IN @ids ORDER BY email_id, position",
                new { ids })
            .GroupBy(a => a.EmailId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AttachmentInfo>)g
                    .Select(a => new AttachmentInfo((int)a.Position, a.FileName ?? "", a.Size, (a.IsInline ?? 0) != 0))
                    .ToList());

        return mails
            .Select(m => new ThreadAttachmentSource(
                m.EntryId, DateTimeOffset.FromUnixTimeSeconds(m.EventTime), m.SenderName, m.SenderEmail, m.Body,
                byMail.TryGetValue(m.Id, out var atts) ? atts : []))
            .ToList();
    }
}
