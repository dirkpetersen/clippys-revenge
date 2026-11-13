using ModelContextProtocol;

namespace ClippysRevenge.Core;

public enum FolderRoot { Inbox, SentItems, StoreRoot }

/// <summary>Canonical: "Inbox", "Sent Items", "Inbox\Projects", "Archive\2025".</summary>
public sealed record ParsedFolderPath(FolderRoot Root, IReadOnlyList<string> SubPath, string Canonical);

/// <summary>A folder resolved on the worker thread; plain data. Mailbox is the normalized key, Path is canonical.</summary>
public sealed record ResolvedFolder(string Mailbox, string Path, string FolderEntryId,
    string StoreId, EventTimeField TimeField);

public static class MailFolderPath
{
    public const string Inbox = "Inbox";
    public const string SentItems = "Sent Items";

    public static ParsedFolderPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segs = path.Replace('/', '\\').Split('\\')
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (segs.Count == 0) throw new ArgumentException("Folder path is empty.", nameof(path));

        var root = FolderRoot.StoreRoot;
        var sub = segs;
        if (segs[0].Equals(Inbox, StringComparison.OrdinalIgnoreCase))
        {
            root = FolderRoot.Inbox;
            segs[0] = Inbox;
            sub = segs.Skip(1).ToList();
        }
        else if (segs[0].Equals(SentItems, StringComparison.OrdinalIgnoreCase))
        {
            root = FolderRoot.SentItems;
            segs[0] = SentItems;
            sub = segs.Skip(1).ToList();
        }
        return new ParsedFolderPath(root, sub, string.Join('\\', segs));
    }

    public static string Canonicalize(string path) => Parse(path).Canonical;

    public static EventTimeField TimeFieldFor(ParsedFolderPath p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Root == FolderRoot.SentItems ? EventTimeField.Sent : EventTimeField.Received;
    }

    /// <summary>Indexed folders of one mailbox from settings, canonical, deduped, blank/invalid skipped.</summary>
    public static IReadOnlyList<string> IndexedFoldersFor(Settings settings, string? mailbox)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var key = MailboxIndexes.Normalize(mailbox);
        var list = key.Length == 0 ? settings.IndexedFolders : settings.FindSharedMailbox(key)?.IndexedFolders;
        if (list is null) return [];

        var result = new List<string>();
        foreach (var raw in list.ToArray())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string canonical;
            try { canonical = Parse(raw).Canonical; }
            catch (ArgumentException) { continue; }
            if (!result.Contains(canonical, StringComparer.OrdinalIgnoreCase)) result.Add(canonical);
        }
        return result;
    }

    /// <summary>Outlook Folder.FolderPath ("\\store\Inbox\Sub") -> canonical path. Null when no segment follows the store.</summary>
    public static string? FromOutlook(string? folderPath, string? inboxName, string? sentItemsName)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        var segs = folderPath.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        if (segs.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(inboxName) && segs[0].Equals(inboxName, StringComparison.OrdinalIgnoreCase))
            segs[0] = Inbox;
        else if (!string.IsNullOrWhiteSpace(sentItemsName) && segs[0].Equals(sentItemsName, StringComparison.OrdinalIgnoreCase))
            segs[0] = SentItems;
        try { return Parse(string.Join('\\', segs)).Canonical; }
        catch (ArgumentException) { return null; }
    }
}

public sealed class MailFolderNotFoundException : McpException
{
    public MailFolderNotFoundException(string mailbox, string path)
        : base($"Folder \"{path}\" was not found in {(string.IsNullOrEmpty(mailbox) ? "your own mailbox" : $"mailbox \"{mailbox}\"")}. Check the indexed folders in the Clippy tray settings.")
    {
        Mailbox = mailbox;
        Path = path;
    }

    public string Mailbox { get; }
    public string Path { get; }
}
