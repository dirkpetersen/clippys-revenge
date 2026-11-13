using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>
/// Read side of the Outlook mail layer (SPEC §8). Every member runs on the OutlookWorker thread only
/// (inside <c>RunAsync</c>). Stateless apart from its dependencies. Reads only; nothing is written to Outlook.
/// </summary>
internal sealed partial class OutlookMail
{
    public const int MaxReadBatch = ComWorker<Outlook.Application>.MaxBackgroundChunk;

    private const string PrSenderSmtp = "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";
    private const string PrSentRepresentingSmtp = "http://schemas.microsoft.com/mapi/proptag/0x5D02001F";
    private const string PrSmtpAddress = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";
    private const string PrAttachHidden = "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B";
    private const string PrAttachContentId = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";
    private const string PrAttachMimeTag = "http://schemas.microsoft.com/mapi/proptag/0x370E001F";
    private const int TableChunk = 200;

    private readonly OutlookStores _stores;
    private readonly MailboxIndexes _indexes;
    private readonly Settings _settings;

    public OutlookMail(OutlookStores stores, MailboxIndexes indexes, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(settings);
        _stores = stores;
        _indexes = indexes;
        _settings = settings;
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    // ---- Folders ----

    /// <summary>Resolves an indexed folder path to plain data. Throws MailFolderNotFoundException or McpException (unknown mailbox).</summary>
    public ResolvedFolder ResolveFolder(Outlook.Application app, string? mailbox, string path)
    {
        var key = MailboxIndexes.Normalize(mailbox);
        var p = MailFolderPath.Parse(path);
        var store = _stores.Get(app, key);
        Outlook.Folder? folder = null;
        try
        {
            folder = p.Root switch
            {
                FolderRoot.Inbox => (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox),
                FolderRoot.SentItems => (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderSentMail),
                _ => (Outlook.Folder)store.GetRootFolder(),
            };
            foreach (var segment in p.SubPath)
            {
                var children = folder.Folders;
                Outlook.Folder? match = null;
                try
                {
                    for (var i = 1; i <= children.Count; i++)
                    {
                        var child = (Outlook.Folder)children[i];
                        if (match is null && string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase))
                            match = child;
                        else
                            Release(child);
                    }
                }
                finally
                {
                    Release(children);
                }
                Release(folder);
                folder = match;
                if (folder is null) throw new MailFolderNotFoundException(key, p.Canonical);
            }
            return new ResolvedFolder(key, p.Canonical, folder.EntryID, store.StoreID, MailFolderPath.TimeFieldFor(p));
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            throw new MailFolderNotFoundException(key, p.Canonical);
        }
        finally
        {
            Release(folder);
            Release(store);
        }
    }

    private static Outlook.Folder OpenFolder(Outlook.Application app, ResolvedFolder rf)
    {
        var ns = app.Session;
        try
        {
            return (Outlook.Folder)ns.GetFolderFromID(rf.FolderEntryId, rf.StoreId);
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            throw new MailFolderNotFoundException(rf.Mailbox, rf.Path);
        }
        finally
        {
            Release(ns);
        }
    }

    // ---- Tables ----

    /// <summary>
    /// Items changed since <paramref name="modifiedFrom"/> (inclusive) whose event time is at or after
    /// <paramref name="cutoff"/>, ascending by LastModified. If HasMore, call again with
    /// modifiedFrom = last.LastModified + 1 s (lossless: the page holds the whole last tie group). When HasMore is
    /// false, store watermark = last.LastModified (inclusive). Rows whose LastModified equals the index's value
    /// are unchanged: don't open them.
    /// </summary>
    public MailTablePage EnumerateChanged(Outlook.Application app, ResolvedFolder folder,
        DateTimeOffset modifiedFrom, DateTimeOffset cutoff, int maxRows = MailTablePager.DefaultPageRows)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(folder);
        ValidateMaxRows(maxRows);
        return WithTable(app, folder, DaslFilter.Changed(folder.TimeField, modifiedFrom, cutoff), MailTable.SortLastModified, false,
            MailTable.Columns(folder.TimeField), rows => MailTableQueries.Changed(rows, modifiedFrom, cutoff, maxRows));
    }

    /// <summary>
    /// Backfill page, newest first, <paramref name="from"/> inclusive, <paramref name="to"/> exclusive. Next call:
    /// to = last.EventTime. Stop when HasMore is false.
    /// </summary>
    public MailTablePage EnumerateRange(Outlook.Application app, ResolvedFolder folder,
        DateTimeOffset from, DateTimeOffset to, int maxRows = MailTablePager.DefaultPageRows)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(folder);
        ValidateMaxRows(maxRows);
        if (OutlookDates.TruncateToSecond(from) >= OutlookDates.TruncateToSecond(to)) return new MailTablePage([], false);
        return WithTable(app, folder, DaslFilter.Range(folder.TimeField, from, to), MailTable.EventTimeSort(folder.TimeField), true,
            MailTable.Columns(folder.TimeField), rows => MailTableQueries.Range(rows, from, to, maxRows));
    }

    /// <summary>Like <see cref="EnumerateRange"/> (newest first, from inclusive, to exclusive, pages up to maxRows), with the thread columns.</summary>
    public MailTablePage EnumerateThreadMeta(Outlook.Application app, ResolvedFolder folder,
        DateTimeOffset from, DateTimeOffset to, int maxRows = MailTablePager.DefaultPageRows)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(folder);
        ValidateMaxRows(maxRows);
        if (OutlookDates.TruncateToSecond(from) >= OutlookDates.TruncateToSecond(to)) return new MailTablePage([], false);
        return WithTable(app, folder, DaslFilter.Range(folder.TimeField, from, to), MailTable.EventTimeSort(folder.TimeField), true,
            MailTable.ThreadColumns(folder.TimeField), rows => MailTableQueries.Range(rows, from, to, maxRows));
    }

    /// <summary>Every indexable entry id (with unread state) whose event time is at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<MailEntryState> ListEntryIds(Outlook.Application app, ResolvedFolder folder, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(folder);
        return WithTable(app, folder, DaslFilter.Since(folder.TimeField, since), null, false,
            MailTable.Columns(folder.TimeField), rows => MailTableQueries.EntryIds(rows, since));
    }

    private static void ValidateMaxRows(int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRows, MailTablePager.MaxPageRows);
    }

    private static T WithTable<T>(Outlook.Application app, ResolvedFolder rf, string filter, string? sort, bool descending,
        IReadOnlyList<string> columns, Func<IEnumerable<IReadOnlyList<object?>>, T> consume)
    {
        var folder = OpenFolder(app, rf);
        try
        {
            var table = folder.GetTable(filter, Outlook.OlTableContents.olUserItems);
            var cols = table.Columns;
            var markerMissing = false;
            try
            {
                cols.RemoveAll();
                foreach (var c in columns)
                {
                    if (c == DraftMarker.PropertyName)
                    {
                        // Outlook may refuse the named property as a column; then the marker reads as null.
                        try { cols.Add(c); }
                        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex)) { markerMissing = true; }
                    }
                    else
                    {
                        cols.Add(c);
                    }
                }
            }
            finally
            {
                Release(cols);
            }
            if (sort is not null) table.Sort(sort, descending);
            var raw = ReadTableRows(table);
            return consume(markerMissing ? raw.Select(r => (IReadOnlyList<object?>)[.. r, null]) : raw);
        }
        finally
        {
            Release(folder);
        }
    }

    /// <summary>Streams the table in chunks; releases the table when the enumeration ends or is disposed.</summary>
    private static IEnumerable<IReadOnlyList<object?>> ReadTableRows(Outlook.Table table)
    {
        try
        {
            while (!table.EndOfTable)
            {
                if (table.GetArray(TableChunk) is not object[,] arr) yield break;
                var r0 = arr.GetLowerBound(0);
                var r1 = arr.GetUpperBound(0);
                var c0 = arr.GetLowerBound(1);
                var c1 = arr.GetUpperBound(1);
                if (r1 < r0) yield break;
                for (var r = r0; r <= r1; r++)
                {
                    var row = new object?[c1 - c0 + 1];
                    for (var c = c0; c <= c1; c++) row[c - c0] = arr[r, c];
                    yield return row;
                }
            }
        }
        finally
        {
            Release(table);
        }
    }

    // ---- Items ----

    /// <summary>Reads up to <see cref="MaxReadBatch"/> items by entry id. Unreadable or vanished ids go to Missing.</summary>
    public MailReadBatch ReadBatch(Outlook.Application app, ResolvedFolder folder, IReadOnlyList<string> entryIds)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(entryIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entryIds.Count, MaxReadBatch);

        var emails = new List<IndexedEmail>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ns = app.Session;
        try
        {
            foreach (var raw in entryIds)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var id = raw.Trim();
                if (!seen.Add(id)) continue;

                object? obj = null;
                try
                {
                    obj = ns.GetItemFromID(id, folder.StoreId);
                    if (obj is Outlook.MailItem item)
                        emails.Add(ReadForIndex(item, folder));
                    else
                        missing.Add(id);
                }
                catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
                {
                    missing.Add(id);
                }
                catch (InvalidOperationException)
                {
                    missing.Add(id);
                }
                finally
                {
                    Release(obj);
                }
            }
        }
        finally
        {
            Release(ns);
        }
        return new MailReadBatch(emails, missing);
    }

    /// <summary>Builds an <see cref="IndexedEmail"/>; the folder supplies the canonical path, store id and time field.</summary>
    public IndexedEmail ReadForIndex(Outlook.MailItem item, ResolvedFolder folder)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(folder);
        var zone = TimeZoneInfo.Local;
        Outlook.PropertyAccessor? pa = null;
        try
        {
            pa = item.PropertyAccessor;
            var imid = ComSafe.Try(() => pa.GetProperty(MailTable.InternetMessageIdProp) as string)?.Trim();
            if (string.IsNullOrEmpty(imid)) imid = null;

            var lastModified = OutlookDates.FromLocal(item.LastModificationTime, zone);
            var eventTime = OutlookDates.FromLocal(
                    folder.TimeField == EventTimeField.Received ? item.ReceivedTime : item.SentOn, zone)
                ?? lastModified
                ?? OutlookDates.FromLocal(item.CreationTime, zone)
                ?? throw new InvalidOperationException("Email has no usable date.");

            var senderEmail = MailAddresses.ChooseSenderSmtp(
                item.SenderEmailType, item.SenderEmailAddress,
                () => ComSafe.Try(() => SenderExchangeSmtp(item)),
                () => ComSafe.Try(() => pa.GetProperty(PrSenderSmtp) as string),
                () => ComSafe.Try(() => pa.GetProperty(PrSentRepresentingSmtp) as string));

            var (to, cc) = ReadRecipients(item);

            var values = ComSafe.Try(() => pa.GetProperties(MailTable.ThreadPropertyNames.Cast<object>().ToArray()) as object[]);
            var meta = values is { Length: 3 } ? MailThreadMeta.FromValues(values[0], values[1], values[2]) : MailThreadMeta.Empty;

            return new IndexedEmail
            {
                EntryId = item.EntryID,
                StoreId = folder.StoreId,
                InternetMessageId = imid,
                Folder = folder.Path,
                Subject = item.Subject,
                SenderName = item.SenderName,
                SenderEmail = senderEmail,
                To = to,
                Cc = cc,
                EventTime = eventTime,
                LastModified = lastModified,
                Unread = item.UnRead,
                Body = item.Body,
                Attachments = ReadAttachments(item),
                InReplyTo = meta.InReplyTo,
                ConversationTopic = meta.ConversationTopic,
                ClippyMarker = meta.ClippyMarker,
            };
        }
        finally
        {
            Release(pa);
        }
    }

    /// <summary>As above, deriving the folder (path, store, mailbox, time field) from <c>item.Parent</c>.</summary>
    public IndexedEmail ReadForIndex(Outlook.MailItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var rf = DeriveFolder(item);
        return ReadForIndex(item, rf);
    }

    private ResolvedFolder DeriveFolder(Outlook.MailItem item)
    {
        Outlook.Folder? parent = null;
        Outlook.Store? store = null;
        Outlook.Folder? inbox = null;
        Outlook.Folder? sent = null;
        try
        {
            parent = (Outlook.Folder)item.Parent;
            store = parent.Store;
            inbox = (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
            sent = (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderSentMail);
            var parentId = parent.EntryID;
            var path = parentId == inbox.EntryID ? MailFolderPath.Inbox
                : parentId == sent.EntryID ? MailFolderPath.SentItems
                : MailFolderPath.FromOutlook(parent.FolderPath, inbox.Name, sent.Name) ?? parent.Name;
            var parsed = MailFolderPath.Parse(path);
            var storeId = store.StoreID;
            var mailbox = "";
            var app = item.Application;
            try
            {
                foreach (var m in _indexes.Mailboxes)
                {
                    if (StoreIdOf(_stores, app, m) == storeId) { mailbox = m; break; }
                }
            }
            finally
            {
                Release(app);
            }
            return new ResolvedFolder(mailbox, parsed.Canonical, parentId, storeId, MailFolderPath.TimeFieldFor(parsed));
        }
        finally
        {
            Release(sent);
            Release(inbox);
            Release(store);
            Release(parent);
        }
    }

    private static string? SenderExchangeSmtp(Outlook.MailItem item)
    {
        Outlook.AddressEntry? sender = null;
        Outlook.ExchangeUser? user = null;
        try
        {
            sender = item.Sender;
            user = sender?.GetExchangeUser();
            return user?.PrimarySmtpAddress;
        }
        finally
        {
            Release(user);
            Release(sender);
        }
    }

    private static (string? To, string? Cc) ReadRecipients(Outlook.MailItem item)
    {
        var to = new List<(string?, string?)>();
        var cc = new List<(string?, string?)>();
        var recipients = item.Recipients;
        try
        {
            var count = recipients.Count;
            for (var i = 1; i <= count; i++)
            {
                if (to.Count >= MailAddresses.MaxRecipientsPerField && cc.Count >= MailAddresses.MaxRecipientsPerField) break;
                var r = recipients[i];
                try
                {
                    var type = r.Type;
                    var list = type == 1 ? to : type == 2 ? cc : null;
                    if (list is null || list.Count >= MailAddresses.MaxRecipientsPerField) continue;
                    var name = r.Name;
                    var smtp = MailAddresses.ChooseRecipientSmtp(
                        () => ComSafe.Try(() => RecipientSmtpProp(r)),
                        ComSafe.Try(() => r.Address),
                        () => ComSafe.Try(() => RecipientExchangeSmtp(r)));
                    list.Add((name, smtp));
                }
                finally
                {
                    Release(r);
                }
            }
        }
        finally
        {
            Release(recipients);
        }
        return (MailAddresses.FormatList(to), MailAddresses.FormatList(cc));
    }

    private static string? RecipientSmtpProp(Outlook.Recipient r)
    {
        var pa = r.PropertyAccessor;
        try { return pa.GetProperty(PrSmtpAddress) as string; }
        finally { Release(pa); }
    }

    private static string? RecipientExchangeSmtp(Outlook.Recipient r)
    {
        Outlook.AddressEntry? entry = null;
        Outlook.ExchangeUser? user = null;
        try
        {
            entry = r.AddressEntry;
            user = entry?.GetExchangeUser();
            return user?.PrimarySmtpAddress;
        }
        finally
        {
            Release(user);
            Release(entry);
        }
    }

    // ---- GetItem chain ----

    /// <summary>SPEC §8 GetItem chain. Throws ItemNotFoundException. The caller releases <c>Item</c>.</summary>
    public ResolvedMailItem<Outlook.MailItem> Resolve(Outlook.Application app, string? id)
    {
        ArgumentNullException.ThrowIfNull(app);
        return MailItemResolver.Resolve(id, _indexes.Locate, _indexes.Mailboxes,
            m => MailFolderPath.IndexedFoldersFor(_settings, m), new ComItemSource(this, app));
    }

    /// <summary>The MailItem for an id. The caller releases it.</summary>
    public Outlook.MailItem GetItem(Outlook.Application app, string? id) => Resolve(app, id).Item;

    /// <summary>Resolves and reads an item live (for the get_message COM fallback). Releases the item.</summary>
    public LiveEmail ReadById(Outlook.Application app, string? id)
    {
        var r = Resolve(app, id);
        try
        {
            return new LiveEmail(r.Mailbox, ReadForIndex(r.Item));
        }
        finally
        {
            Release(r.Item);
        }
    }

    private static string? StoreIdOf(OutlookStores stores, Outlook.Application app, string mailbox)
    {
        Outlook.Store? store = null;
        try
        {
            store = stores.Get(app, mailbox);
            return store.StoreID;
        }
        catch (McpException)
        {
            return null;
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return null;
        }
        finally
        {
            Release(store);
        }
    }

    private sealed class ComItemSource : IMailItemSource<Outlook.MailItem>
    {
        private readonly OutlookMail _owner;
        private readonly Outlook.Application? _app;

        public ComItemSource(OutlookMail owner, Outlook.Application? app)
        {
            _owner = owner;
            _app = app;
        }

        public string? StoreIdFor(string mailbox) => OutlookMail.StoreIdOf(_owner._stores, _app!, mailbox);

        public Outlook.MailItem? TryGetById(string entryId, string storeId)
        {
            var ns = _app!.Session;
            object? obj = null;
            try
            {
                obj = ns.GetItemFromID(entryId, storeId);
                if (obj is Outlook.MailItem item)
                {
                    obj = null;
                    return item;
                }
                return null;
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                return null;
            }
            finally
            {
                Release(obj);
                Release(ns);
            }
        }

        public string? FindByMessageId(string mailbox, string folderPath, string internetMessageId)
        {
            ResolvedFolder rf;
            try
            {
                rf = _owner.ResolveFolder(_app!, mailbox, folderPath);
            }
            catch (MailFolderNotFoundException)
            {
                return null;
            }
            var folder = OpenFolder(_app!, rf);
            Outlook.Table? table = null;
            try
            {
                table = folder.GetTable(DaslFilter.ByMessageId(internetMessageId), Outlook.OlTableContents.olUserItems);
                var cols = table.Columns;
                try
                {
                    cols.RemoveAll();
                    cols.Add(MailTable.EntryIdColumn);
                    cols.Add(MailTable.LongTermEntryIdProp);
                }
                finally
                {
                    Release(cols);
                }
                if (table.EndOfTable) return null;
                var row = table.GetNextRow();
                try
                {
                    return MailTable.ParseEntryId(row[2], row[1]);
                }
                finally
                {
                    Release(row);
                }
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                return null;
            }
            finally
            {
                Release(table);
                Release(folder);
            }
        }
    }

    // ---- Attachments (metadata only, never content) ----

    /// <summary>Attachment metadata in position order (position = COM Attachment.Index, 1-based).</summary>
    internal static IReadOnlyList<AttachmentInfo> ReadAttachments(Outlook.MailItem item)
    {
        var result = new List<AttachmentInfo>();
        var html = new Lazy<string?>(() => ComSafe.Try(() => item.HTMLBody));
        var atts = item.Attachments;
        try
        {
            var count = atts.Count;
            for (var i = 1; i <= count; i++)
            {
                var a = atts[i];
                try
                {
                    var position = a.Index;
                    var facts = ReadFacts(a);
                    result.Add(new AttachmentInfo(position, facts.FileName,
                        ComSafe.Try<long?>(() => (long)a.Size),
                        InlineAttachments.IsInline(facts, () => html.Value)));
                }
                finally
                {
                    Release(a);
                }
            }
        }
        finally
        {
            Release(atts);
        }
        return result;
    }

    internal static bool IsInline(Outlook.Attachment attachment, Func<string?> htmlBody)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return InlineAttachments.IsInline(ReadFacts(attachment), htmlBody);
    }

    internal static bool IsInline(Outlook.Attachment attachment, string? htmlBody) =>
        IsInline(attachment, () => htmlBody);

    private static AttachmentFacts ReadFacts(Outlook.Attachment a)
    {
        var pa = ComSafe.Try(() => a.PropertyAccessor);
        try
        {
            var hidden = pa is not null && ComSafe.Try(() => pa.GetProperty(PrAttachHidden)) is bool b && b;
            var cid = pa is null ? null : ComSafe.Try(() => pa.GetProperty(PrAttachContentId) as string);
            var mime = pa is null ? null : ComSafe.Try(() => pa.GetProperty(PrAttachMimeTag) as string);
            var fileName = InlineAttachments.FileNameFor(
                ComSafe.Try(() => a.FileName), ComSafe.Try(() => a.DisplayName), ComSafe.Try(() => a.Index));
            return new AttachmentFacts((int)a.Type, hidden, cid, mime, fileName);
        }
        finally
        {
            Release(pa);
        }
    }
}
