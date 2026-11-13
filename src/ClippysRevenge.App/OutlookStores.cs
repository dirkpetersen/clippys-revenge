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

    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AttachmentTimeout = TimeSpan.FromSeconds(120);

    private readonly OutlookWorker _worker;
    private readonly Func<Settings> _settings;
    private readonly StoreListCache _cache = new();

    public OutlookStores(OutlookWorker worker, Func<Settings> settings)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(settings);
        _worker = worker;
        _settings = settings;
    }

    /// <summary>
    /// Mounted Exchange mailboxes (own first as Outlook lists them), cached per worker connection.
    /// Local properties only: SMTPs come from Session.Accounts and earlier lookups, never from the GAL.
    /// Releases every COM object it touches. Worker thread.
    /// </summary>
    public IReadOnlyList<MailboxStore> List(Outlook.Application app)
    {
        var connection = _worker.ConnectionId;
        if (_cache.TryGet(connection, out var cached))
            return cached;

        var candidates = new List<StoreCandidate>();
        var accountSmtp = new Dictionary<string, string>(StringComparer.Ordinal);
        Outlook.NameSpace? session = null;
        Outlook.Stores? stores = null;
        try
        {
            session = app.Session;
            stores = session.Stores;
            var defaultId = ReadDefaultStoreId(session);
            ReadAccountSmtps(session, accountSmtp);
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
                        store.DisplayName ?? "", id, accountSmtp.GetValueOrDefault(id),
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
            if (stores is not null) Marshal.ReleaseComObject(stores);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
        return _cache.Set(connection, candidates);
    }

    /// <summary>The GAL owner lookup for one store. Its own work item, never part of List or Get. Worker thread.</summary>
    public string? LookupOwnerSmtp(Outlook.Application app, string storeId)
    {
        var connection = _worker.ConnectionId;
        Outlook.NameSpace? session = null;
        Outlook.Store? store = null;
        Outlook.PropertyAccessor? pa = null;
        Outlook.AddressEntry? entry = null;
        Outlook.ExchangeUser? user = null;
        string? smtp = null;
        try
        {
            session = app.Session;
            try
            {
                store = session.GetStoreFromID(storeId);
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                store = null;
            }
            if (store is not null)
            {
                // PR_MAILBOX_OWNER_ENTRYID, then the GAL (the call that can hang offline).
                try
                {
                    pa = store.PropertyAccessor;
                    var bytes = (byte[])pa.GetProperty(PrStoreOwnerEntryId);
                    entry = session.GetAddressEntryFromID(pa.BinaryToString(bytes));
                    user = entry.GetExchangeUser();
                    smtp = user?.PrimarySmtpAddress;
                }
                catch (Exception ex) when (ex is (COMException or InvalidCastException or ArgumentException) && !ComErrors.IsOutlookGone(ex))
                {
                    smtp = null;
                }
            }
        }
        finally
        {
            if (user is not null) Marshal.ReleaseComObject(user);
            if (entry is not null) Marshal.ReleaseComObject(entry);
            if (pa is not null) Marshal.ReleaseComObject(pa);
            if (store is not null) Marshal.ReleaseComObject(store);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
        _cache.RecordLookup(connection, storeId, smtp);
        return smtp;
    }

    /// <summary>
    /// Thread pool. Makes sure every enabled shared mailbox is matched to a mounted store (or has been tried)
    /// on the current connection. No work item at all when nothing is pending.
    /// </summary>
    public async Task EnsureResolvedAsync(WorkPriority priority, CancellationToken ct, bool includeUnconfigured = false)
    {
        var shared = ConfiguredKeys().Where(k => k.Length > 0).ToList();
        if (shared.Count == 0 && !includeUnconfigured)
            return;
        if (_cache.IsSettled(_worker.ConnectionId, shared, includeUnconfigured))
            return;

        var pending = await _worker.RunAsync(app =>
        {
            List(app);
            return _cache.PendingLookups(_worker.ConnectionId, shared, includeUnconfigured);
        }, priority, ct).ConfigureAwait(false);

        foreach (var id in pending)
        {
            try
            {
                await _worker.RunAsync(app => LookupOwnerSmtp(app, id), priority, ct, LookupTimeout).ConfigureAwait(false);
            }
            catch (OutlookNotRespondingException)
            {
                _cache.RecordTimeout(id);
                throw;
            }
            if (_cache.IsSettled(_worker.ConnectionId, shared, includeUnconfigured))
                break;
        }
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

    private static void ReadAccountSmtps(Outlook.NameSpace session, Dictionary<string, string> map)
    {
        Outlook.Accounts? accounts = null;
        try
        {
            accounts = session.Accounts;
            foreach (Outlook.Account account in accounts)
            {
                Outlook.Store? delivery = null;
                try
                {
                    delivery = account.DeliveryStore;
                    var id = delivery?.StoreID;
                    var smtp = account.SmtpAddress;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(smtp))
                        map.TryAdd(id, smtp);
                }
                catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
                {
                    // Skip this account.
                }
                finally
                {
                    if (delivery is not null) Marshal.ReleaseComObject(delivery);
                    Marshal.ReleaseComObject(account);
                }
            }
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            // No account data: SMTPs stay unknown.
        }
        finally
        {
            if (accounts is not null) Marshal.ReleaseComObject(accounts);
        }
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
}
