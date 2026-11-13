namespace ClippysRevenge.Core;

/// <summary>
/// The COM side of sync. Every method may block while Outlook is unavailable (background priority)
/// and honors ct. Implementations never modify items.
/// </summary>
public interface IMailSource
{
    /// <summary>"" = own mailbox, otherwise a normalized SMTP key. Null when it isn't mounted in Outlook.</summary>
    Task<SourceMailbox?> OpenMailboxAsync(string mailbox, CancellationToken ct);

    /// <summary>folderPath is <see cref="FolderPaths.Canonical"/> form. Null when the folder doesn't exist.</summary>
    Task<SourceFolder?> GetFolderAsync(SourceMailbox mailbox, string folderPath, CancellationToken ct);

    /// <summary>
    /// Rows with LastModified &gt;= modifiedSince (inclusive; may also return older rows) and
    /// EventTime &gt;= cutoff. Any order.
    /// </summary>
    Task<IReadOnlyList<MailRow>> ListChangedAsync(SourceFolder folder, DateTimeOffset modifiedSince, DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>Rows with from &lt;= EventTime &lt;= to (inclusive). Any order.</summary>
    Task<IReadOnlyList<MailRow>> ListRangeAsync(SourceFolder folder, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>At most <see cref="SyncEngine.BatchSize"/> ids. Items that no longer exist or aren't mail items are omitted.</summary>
    Task<IReadOnlyList<IndexedEmail>> ReadAsync(SourceFolder folder, IReadOnlyList<string> entryIds, CancellationToken ct);

    /// <summary>
    /// One page for the thread-metadata refresh (SPEC 10): rows with from &lt;= EventTime &lt; toExclusive, newest first,
    /// at most 500, never splitting a group of equal EventTimes. Next page: toExclusive = last row's EventTime.
    /// Table-only: no item is opened.
    /// </summary>
    Task<ThreadMetaPage> ListThreadMetaAsync(SourceFolder folder, DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken ct);
}

public sealed record ThreadMetaRow(string EntryId, DateTimeOffset EventTime, string? InReplyTo, string? ConversationTopic, string? ClippyMarker);

public sealed record ThreadMetaPage(IReadOnlyList<ThreadMetaRow> Rows, bool HasMore);

/// <summary>Outlook went away mid-cycle. The engine propagates it; the service aborts the cycle quietly.</summary>
public sealed class MailSourceUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record SourceMailbox(string Key, string Smtp, string StoreId, string DisplayName);

/// <param name="FolderKey">The folder's EntryID.</param>
/// <param name="Path">Canonical path.</param>
public sealed record SourceFolder(string FolderKey, string Path, string StoreId, bool IsSent);

public sealed record MailRow(string EntryId, DateTimeOffset? LastModified, DateTimeOffset EventTime, bool Unread);

/// <param name="Mailbox">"" or a normalized SMTP key.</param>
/// <param name="Folders">Canonical, distinct.</param>
public sealed record MailboxPlan(string Mailbox, IReadOnlyList<string> Folders, int RetentionMonths);

public sealed record FolderSyncResult(string Folder, bool Missing, int Upserted, int UnreadUpdated, bool BackfillComplete);

public sealed record MailboxSyncResult(
    string Mailbox, bool NotMounted, IReadOnlyList<FolderSyncResult> Folders,
    int Purged, bool BackfillFinished, TimeSpan Duration);

public sealed record SyncCycleResult(IReadOnlyList<MailboxSyncResult> Mailboxes, bool Aborted);

public static class FolderPaths
{
    public const string Inbox = "Inbox";
    public const string SentItems = "Sent Items";

    /// <summary>
    /// Trim; '/' to '\'; drop leading/trailing/repeated '\'; first segment "inbox" / "sent items"
    /// (any case) to "Inbox" / "Sent Items". ArgumentException when the result is empty.
    /// </summary>
    public static string Canonical(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return MailFolderPath.Parse(path).Canonical;
    }

    /// <summary>True when the first segment of the canonical path is "Sent Items".</summary>
    public static bool IsUnderSentItems(string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        var first = canonicalPath.Split('\\', 2)[0];
        return first.Equals(SentItems, StringComparison.Ordinal);
    }
}

public static class SyncLabels
{
    /// <summary>"" to ""; "helpdesk@x.edu" to "helpdesk@…".</summary>
    public static string MailboxPrefix(string mailboxKey)
    {
        ArgumentNullException.ThrowIfNull(mailboxKey);
        if (mailboxKey.Length == 0) return "";
        var at = mailboxKey.IndexOf('@');
        return (at < 0 ? mailboxKey : mailboxKey[..at]) + "@…";
    }

    /// <summary>"" to "your mailbox"; otherwise the key.</summary>
    public static string MailboxName(string mailboxKey)
    {
        ArgumentNullException.ThrowIfNull(mailboxKey);
        return mailboxKey.Length == 0 ? "your mailbox" : mailboxKey;
    }

    /// <summary>"Sent Items" to "Sent"; anything else unchanged.</summary>
    public static string FolderLabel(string canonicalPath) =>
        canonicalPath == FolderPaths.SentItems ? "Sent" : canonicalPath;
}

public static class SyncPlanner
{
    /// <summary>
    /// Own mailbox first, then every enabled shared mailbox in list order. Keys are normalized; duplicate
    /// keys keep the first; entries without a valid address are skipped; folders are canonical and
    /// distinct (case-insensitive, first wins); blank folders are skipped. Never throws on bad entries.
    /// </summary>
    public static IReadOnlyList<MailboxPlan> Build(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plans = new List<MailboxPlan>
        {
            new(MailboxIndexes.OwnMailbox, CanonicalFolders(settings.IndexedFolders), settings.RetentionMonths),
        };
        var seen = new HashSet<string>(StringComparer.Ordinal) { MailboxIndexes.OwnMailbox };
        if (settings.SharedMailboxes is null) return plans;
        foreach (var sm in settings.SharedMailboxes.ToArray())
        {
            if (sm is null || !sm.Enabled) continue;
            var key = MailboxIndexes.Normalize(sm.Smtp);
            if (key.Length == 0 || key == MailboxIndexes.AllMailboxes || !key.Contains('@')) continue;
            if (!seen.Add(key)) continue;
            plans.Add(new MailboxPlan(key, CanonicalFolders(sm.IndexedFolders), sm.EffectiveRetentionMonths(settings.RetentionMonths)));
        }
        return plans;
    }

    private static IReadOnlyList<string> CanonicalFolders(List<string>? raw)
    {
        var result = new List<string>();
        if (raw is null) return result;
        foreach (var f in raw.ToArray())
        {
            if (string.IsNullOrWhiteSpace(f)) continue;
            string canonical;
            try { canonical = FolderPaths.Canonical(f); }
            catch (ArgumentException) { continue; }
            if (!result.Contains(canonical, StringComparer.OrdinalIgnoreCase)) result.Add(canonical);
        }
        return result;
    }
}
