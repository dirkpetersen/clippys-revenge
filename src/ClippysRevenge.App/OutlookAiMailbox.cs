using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>
/// The COM side of AI drafting (SPEC 19.9). Every call runs on the Outlook worker; every COM object is released.
/// Reads only, plus showing the Drafts folder in the active window. Logs hold method names and outcomes, never mail data.
/// </summary>
internal sealed class OutlookAiMailbox(OutlookWorker worker, OutlookMail mail, OutlookStores stores, ILogger? log = null)
    : IAiMailbox
{
    private const int MaxSelectionScan = 50;

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    public async Task<CandidateFacts?> GetFactsAsync(string mailbox, string entryId, WorkPriority priority, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(priority, ct).ConfigureAwait(false);
        var facts = await worker.RunAsync(app => GetFacts(app, mailbox, entryId), priority, ct).ConfigureAwait(false);
        log?.LogDebug("AI mailbox: GetFacts {Outcome}", facts is null ? "gone" : "ok");
        return facts;
    }

    public Task<AiIdentity> GetIdentityAsync(WorkPriority priority, CancellationToken ct) =>
        worker.RunAsync(GetIdentity, priority, ct);

    public async Task<SelectedEmail?> GetSelectedAsync(CancellationToken ct)
    {
        var sel = await worker.RunAsync(GetSelected, WorkPriority.Interactive, ct).ConfigureAwait(false);
        log?.LogDebug("AI mailbox: GetSelected {Outcome}", sel is null ? "none" : "ok");
        return sel;
    }

    public async Task<LiveEmail?> ReadEmailAsync(string entryId, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        try
        {
            return await worker.RunAsync(app => mail.ReadById(app, entryId), WorkPriority.Interactive, ct).ConfigureAwait(false);
        }
        catch (ItemNotFoundException)
        {
            log?.LogDebug("AI mailbox: ReadEmail gone");
            return null;
        }
    }

    public async Task<bool> ShowDraftsAsync(CancellationToken ct)
    {
        try
        {
            var shown = await worker.RunAsync(ShowDrafts, WorkPriority.Interactive, ct).ConfigureAwait(false);
            log?.LogDebug("AI mailbox: ShowDrafts {Outcome}", shown ? "shown" : "no window");
            return shown;
        }
        catch (OutlookUnavailableException)
        {
            return false;
        }
    }

    // ---- worker-thread bodies ----

    internal CandidateFacts? GetFacts(Outlook.Application app, string mailbox, string entryId)
    {
        ResolvedMailItem<Outlook.MailItem> r;
        try
        {
            r = mail.Resolve(app, entryId);
        }
        catch (ItemNotFoundException)
        {
            return null;
        }

        var item = r.Item;
        Outlook.PropertyAccessor? pa = null;
        try
        {
            var messageClass = item.MessageClass;
            pa = item.PropertyAccessor;
            var values = ComSafe.Try(() => pa.GetProperties(
                new object[] { MailTable.InternetMessageIdProp, MailTable.ConversationTopicProp, MailTable.TransportHeadersProp }) as object[]);
            var imid = Clean(values, 0);
            var topic = Clean(values, 1);
            var headers = Clean(values, 2);
            if (headers is { Length: > DraftRules.MaxHeaderChars }) headers = headers[..DraftRules.MaxHeaderChars];
            var verb = ComSafe.Try(() => pa.GetProperty(MailTable.LastVerbProp)) is int v ? v : (int?)null;
            var draftExists = DraftExists(app, mailbox, imid, topic);
            return new CandidateFacts(messageClass, imid, topic, headers, verb, draftExists);
        }
        finally
        {
            Release(pa);
            Release(item);
        }
    }

    private static string? Clean(object[]? values, int i)
    {
        if (values is null || i >= values.Length || values[i] is not string s) return null;
        var t = s.Trim();
        return t.Length == 0 ? null : t;
    }

    private bool DraftExists(Outlook.Application app, string mailbox, string? imid, string? topic)
    {
        var filter = DaslFilter.DraftMatch(imid, topic);
        if (filter is null) return false;
        Outlook.Store? store = null;
        Outlook.MAPIFolder? drafts = null;
        Outlook.Table? table = null;
        try
        {
            store = stores.Get(app, mailbox);
            drafts = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDrafts);
            table = drafts.GetTable(filter, Outlook.OlTableContents.olUserItems);
            return !table.EndOfTable;
        }
        finally
        {
            Release(table);
            Release(drafts);
            Release(store);
        }
    }

    internal AiIdentity GetIdentity(Outlook.Application app)
    {
        Outlook.NameSpace? session = null;
        Outlook.Recipient? user = null;
        try
        {
            session = app.Session;
            var name = "";
            try
            {
                user = session.CurrentUser;
                name = user.Name ?? "";
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                name = "";
            }
            var own = StoreCatalog.Find(stores.List(app), "")?.Smtp ?? "";
            return new AiIdentity(name, own);
        }
        finally
        {
            Release(user);
            Release(session);
        }
    }

    internal SelectedEmail? GetSelected(Outlook.Application app)
    {
        Outlook.Explorer? explorer = null;
        Outlook.Selection? selection = null;
        try
        {
            explorer = app.ActiveExplorer();
            if (explorer is null) return null;
            selection = explorer.Selection;
            var count = Math.Min(selection.Count, MaxSelectionScan);
            for (var i = 1; i <= count; i++)
            {
                object? item = null;
                try
                {
                    item = selection[i];
                    if (item is not Outlook.MailItem m) continue;
                    var id = m.EntryID;
                    string? storeId = null;
                    Outlook.Folder? parent = null;
                    try
                    {
                        parent = (Outlook.Folder)m.Parent;
                        storeId = parent.StoreID;
                    }
                    catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
                    {
                        storeId = null;
                    }
                    finally
                    {
                        Release(parent);
                    }
                    return new SelectedEmail(id, storeId);
                }
                finally
                {
                    Release(item);
                }
            }
            return null;
        }
        finally
        {
            Release(selection);
            Release(explorer);
        }
    }

    internal bool ShowDrafts(Outlook.Application app)
    {
        Outlook.Explorer? explorer = null;
        Outlook.NameSpace? session = null;
        Outlook.MAPIFolder? drafts = null;
        try
        {
            explorer = app.ActiveExplorer();
            if (explorer is null) return false;
            session = app.Session;
            drafts = session.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDrafts);
            explorer.CurrentFolder = drafts;
            explorer.Activate();
            return true;
        }
        finally
        {
            Release(drafts);
            Release(session);
            Release(explorer);
        }
    }
}
