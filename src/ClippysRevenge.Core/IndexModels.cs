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
