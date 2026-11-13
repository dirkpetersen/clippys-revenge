namespace ClippysRevenge.Core;

/// <summary>One attachment's metadata. Never its content (SPEC §1.6, §9.1).</summary>
public sealed record AttachmentInfo(int Position, string FileName, long? Size, bool IsInline);

/// <summary>One email as read from Outlook for indexing (SPEC §8 ReadForIndex).</summary>
public sealed record IndexedEmail
{
    public required string EntryId { get; init; }
    public string? StoreId { get; init; }
    public string? InternetMessageId { get; init; }
    public required string Folder { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderEmail { get; init; }
    public string? To { get; init; }
    public string? Cc { get; init; }
    public required DateTimeOffset EventTime { get; init; }      // ReceivedTime (Inbox) / SentOn (Sent)
    public DateTimeOffset? LastModified { get; init; }
    public bool Unread { get; init; }
    public string? Body { get; init; }
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
    public string? InReplyTo { get; init; }          // PR_IN_REPLY_TO_ID
    public string? ConversationTopic { get; init; }  // PR_CONVERSATION_TOPIC
    public string? ClippyMarker { get; init; }       // "ai" | "tool" | null (SPEC 19.4)
}

/// <summary>One indexed email as stored (SPEC §9.2 Get, §11.2 get_message).</summary>
public sealed record EmailDetail
{
    public required long RowId { get; init; }
    public required string EntryId { get; init; }
    public string? StoreId { get; init; }
    public string? InternetMessageId { get; init; }
    public required string Folder { get; init; }
    public string? Subject { get; init; }
    public string? SenderName { get; init; }
    public string? SenderEmail { get; init; }
    public string? To { get; init; }
    public string? Cc { get; init; }
    public required DateTimeOffset EventTime { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public required bool Unread { get; init; }
    public required bool HasAttachments { get; init; }
    public required IReadOnlyList<AttachmentInfo> Attachments { get; init; }
    public string? Body { get; init; }
    public string? InReplyTo { get; init; }
    public string? ConversationTopic { get; init; }
    public string? ClippyMarker { get; init; }
}

/// <summary>Per-folder sync bookkeeping (SPEC §9.1 sync_state). Times are UTC.</summary>
public sealed record SyncState(
    string FolderKey,
    string? FolderPath = null,
    DateTimeOffset? ModifiedWatermark = null,
    DateTimeOffset? OldestSynced = null,
    DateTimeOffset? LastReconcile = null,
    bool BackfillComplete = false);

/// <summary>What sync needs to know about an indexed row to decide whether to read it again.</summary>
public sealed record IndexedRowInfo(DateTimeOffset? LastModified, bool Unread);

/// <summary>Index statistics for one mailbox (SPEC §9.2 Status).</summary>
public sealed record IndexStatus(
    long TotalEmails,
    IReadOnlyDictionary<string, long> FolderCounts,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest,
    long DbSizeBytes,
    DateTimeOffset? LastSync,
    bool BackfillComplete);

/// <summary>The three thread properties as read from Outlook (SPEC 8, 19.4).</summary>
public sealed record MailThreadMeta(string? InReplyTo, string? ConversationTopic, string? ClippyMarker)
{
    public static MailThreadMeta Empty { get; } = new(null, null, null);

    /// <summary>
    /// Raw COM values (from GetProperties or a Table row) to normalized meta. A value that isn't a string
    /// (an int HRESULT for a missing property, DBNull, byte[], null) gives null. InReplyTo and ConversationTopic
    /// are trimmed, and "" after trimming gives null. ClippyMarker = DraftMarker.Normalize(value as string).
    /// Never throws.
    /// </summary>
    public static MailThreadMeta FromValues(object? inReplyTo, object? conversationTopic, object? clippyMarker) =>
        new(Clean(inReplyTo), Clean(conversationTopic), DraftMarker.Normalize(clippyMarker as string));

    private static string? Clean(object? v)
    {
        if (v is not string s) return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }
}

/// <summary>One earlier message of a conversation for the drafting prompt (SPEC 19.7). No attachment content.</summary>
public sealed record ThreadMessage(
    string EntryId, string Folder, DateTimeOffset EventTime, string? SenderName, string? SenderEmail,
    string? To, string? Cc, string? Subject, IReadOnlyList<string> AttachmentNames, string? Body);

/// <summary>A sent email for style sampling (SPEC 19.6).</summary>
public sealed record SentMail(
    string EntryId, string? InternetMessageId, DateTimeOffset EventTime, string? Subject,
    string? To, string? Cc, string? Body);

/// <summary>An Inbox row the drafting worker may decide on (SPEC 19.7 candidates).</summary>
public sealed record DraftCandidate(
    string EntryId, string? StoreId, string? InternetMessageId, string? ConversationTopic, DateTimeOffset EventTime,
    string? Subject, string? SenderName, string? SenderEmail, string? To, string? Cc);

public enum AiOutcome { Drafted, NoReply, LowConfidence, Skipped, Failed }

public static class AiOutcomes
{
    /// <exception cref="ArgumentOutOfRangeException">Undefined value.</exception>
    public static string ToDb(AiOutcome outcome) => outcome switch
    {
        AiOutcome.Drafted => "drafted",
        AiOutcome.NoReply => "no_reply",
        AiOutcome.LowConfidence => "low_confidence",
        AiOutcome.Skipped => "skipped",
        AiOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>Exact (ordinal) inverse of ToDb. Any other value, including null, gives Skipped (fail-safe: never re-drafted).</summary>
    public static AiOutcome FromDb(string? value) => value switch
    {
        "drafted" => AiOutcome.Drafted,
        "no_reply" => AiOutcome.NoReply,
        "low_confidence" => AiOutcome.LowConfidence,
        "skipped" => AiOutcome.Skipped,
        "failed" => AiOutcome.Failed,
        _ => AiOutcome.Skipped,
    };
}

/// <summary>One ai_decisions row (SPEC 9.1). Ids, outcome and numbers only. Times are UTC whole seconds.
/// ConversationTopic is always written as null and never read (SPEC 19.11).</summary>
public sealed record AiDecision(
    string SourceEntryId,
    string? SourceImid,
    string? ConversationTopic,
    DateTimeOffset Decided,
    AiOutcome Outcome,
    string? Rule = null,
    int ModelCalls = 0,
    int Attempts = 0,
    double? Confidence = null,
    string? DraftEntryId = null,
    DateTimeOffset? DraftCreated = null);
