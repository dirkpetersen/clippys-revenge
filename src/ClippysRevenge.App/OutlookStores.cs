using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>
/// Mounted Exchange mailboxes of the current Outlook profile (SPEC §8). Every method must run on the
/// worker thread (inside <c>OutlookWorker.RunAsync</c>).
/// </summary>
internal sealed class OutlookStores
{
    private const string PrStoreOwnerEntryId = "http://schemas.microsoft.com/mapi/proptag/0x661B0102";

    private readonly Func<Settings> _settings;

    public OutlookStores(Func<Settings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Mounted Exchange mailboxes (own first as Outlook lists them). Releases every COM object it touches.</summary>
    public IReadOnlyList<MailboxStore> List(Outlook.Application app)
    {
        var candidates = new List<StoreCandidate>();
        var session = app.Session;
        var stores = session.Stores;
        try
        {
            var defaultId = ReadDefaultStoreId(session);
            foreach (Outlook.Store store in stores)
            {
                try
                {
                    int type;
                    try { type = (int)store.ExchangeStoreType; }
                    catch (COMException ex) when (!ComErrors.IsOutlookGone(ex)) { continue; }
                    if (!StoreCatalog.IsMailboxType(type)) continue;
                    var id = store.StoreID;
                    candidates.Add(new StoreCandidate(
                        store.DisplayName ?? "", id, ReadOwnerSmtp(session, store),
                        type, defaultId is not null && string.Equals(id, defaultId, StringComparison.Ordinal)));
                }
                finally
                {
                    Marshal.ReleaseComObject(store);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(stores);
            Marshal.ReleaseComObject(session);
        }
        return StoreCatalog.Filter(candidates);
    }

    /// <summary>
    /// The <c>Store</c> for "" (own) or an SMTP address. The caller releases it.
    /// Throws <see cref="McpException"/> listing the configured mailboxes when the address is not one of them,
    /// or saying it is not open in Outlook when it is configured but not mounted.
    /// </summary>
    public Outlook.Store Get(Outlook.Application app, string? mailbox)
    {
        var key = EmailAddress.Normalize(mailbox);
        var configured = ConfiguredKeys();
        if (key == MailboxIndexes.AllMailboxes || !configured.Contains(key, StringComparer.Ordinal))
            throw MailboxIndexes.Unknown(mailbox, configured);

        var found = StoreCatalog.Find(List(app), key);
        if (found is null)
            throw key.Length == 0
                ? new McpException("Your own Exchange mailbox is not open in Outlook.")
                : StoreCatalog.NotAvailable(mailbox, configured);

        var session = app.Session;
        try
        {
            return session.GetStoreFromID(found.StoreId);
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            throw StoreCatalog.NotAvailable(mailbox, configured);
        }
        finally
        {
            Marshal.ReleaseComObject(session);
        }
    }

    private const int MaxFolderDepth = 6, MaxFolders = 2000;

    /// <summary>Canonical paths (MailFolderPath.FromOutlook) of every mail folder of the mailbox's store,
    /// depth up to 6, at most 2000 folders. Resolves the store from List(app) by key WITHOUT the
    /// "configured" check of Get (a mailbox being added in Settings isn't configured yet).</summary>
    /// <exception cref="McpException">The mailbox is not open in Outlook.</exception>
    public IReadOnlyList<string> ListMailFolders(Outlook.Application app, string? mailbox)
    {
        var key = EmailAddress.Normalize(mailbox);
        var found = StoreCatalog.Find(List(app), key);
        if (found is null)
            throw key.Length == 0
                ? new McpException("Your own Exchange mailbox is not open in Outlook.")
                : StoreCatalog.NotAvailable(mailbox, [key]);

        var session = app.Session;
        Outlook.Store? store = null;
        Outlook.Folder? inbox = null, sent = null;
        Outlook.Folder? root = null;
        try
        {
            try { store = session.GetStoreFromID(found.StoreId); }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                throw StoreCatalog.NotAvailable(mailbox, [key]);
            }
            string? inboxName = null, sentName = null;
            try
            {
                inbox = (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderInbox);
                inboxName = inbox.Name;
                sent = (Outlook.Folder)store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderSentMail);
                sentName = sent.Name;
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex)) { }

            var result = new List<string>();
            root = (Outlook.Folder)store.GetRootFolder();
            Walk(root, 0, inboxName, sentName, result);
            return result;
        }
        finally
        {
            if (root is not null) Marshal.ReleaseComObject(root);
            if (sent is not null) Marshal.ReleaseComObject(sent);
            if (inbox is not null) Marshal.ReleaseComObject(inbox);
            if (store is not null) Marshal.ReleaseComObject(store);
            Marshal.ReleaseComObject(session);
        }
    }

    private static void Walk(Outlook.Folder parent, int depth, string? inboxName, string? sentName, List<string> into)
    {
        if (depth >= MaxFolderDepth) return;
        var children = parent.Folders;
        try
        {
            foreach (Outlook.Folder f in children)
            {
                try
                {
                    if (into.Count >= MaxFolders) return;
                    if (f.DefaultItemType == Outlook.OlItemType.olMailItem
                        && MailFolderPath.FromOutlook(f.FolderPath, inboxName, sentName) is { } path)
                        into.Add(path);
                    Walk(f, depth + 1, inboxName, sentName, into);
                }
                catch (COMException ex) when (!ComErrors.IsOutlookGone(ex)) { }
                finally
                {
                    Marshal.ReleaseComObject(f);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(children);
        }
    }

    private IReadOnlyList<string> ConfiguredKeys()
    {
        var keys = new List<string> { MailboxIndexes.OwnMailbox };
        var s = _settings();
        if (s.SharedMailboxes is not null)
            foreach (var sm in s.SharedMailboxes)
            {
                if (sm is null || !sm.Enabled) continue;
                var k = EmailAddress.Normalize(sm.Smtp);
                if (k.Length > 0 && !keys.Contains(k, StringComparer.Ordinal)) keys.Add(k);
            }
        return keys;
    }

    private static string? ReadDefaultStoreId(Outlook.NameSpace session)
    {
        Outlook.Store? def = null;
        try
        {
            def = session.DefaultStore;
            return def?.StoreID;
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return null;
        }
        finally
        {
            if (def is not null) Marshal.ReleaseComObject(def);
        }
    }

    /// <summary>Owner SMTP via PR_MAILBOX_OWNER_ENTRYID, then AddressEntry.GetExchangeUser().PrimarySmtpAddress.</summary>
    private static string? ReadOwnerSmtp(Outlook.NameSpace session, Outlook.Store store)
    {
        Outlook.PropertyAccessor? pa = null;
        Outlook.AddressEntry? entry = null;
        Outlook.ExchangeUser? user = null;
        try
        {
            pa = store.PropertyAccessor;
            var bytes = (byte[])pa.GetProperty(PrStoreOwnerEntryId);
            entry = session.GetAddressEntryFromID(pa.BinaryToString(bytes));
            user = entry.GetExchangeUser();
            return user?.PrimarySmtpAddress;
        }
        catch (Exception ex) when (ex is (COMException or InvalidCastException or ArgumentException) && !ComErrors.IsOutlookGone(ex))
        {
            return null;
        }
        finally
        {
            if (user is not null) Marshal.ReleaseComObject(user);
            if (entry is not null) Marshal.ReleaseComObject(entry);
            if (pa is not null) Marshal.ReleaseComObject(pa);
        }
    }
}
