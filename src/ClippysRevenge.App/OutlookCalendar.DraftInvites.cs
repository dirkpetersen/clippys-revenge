using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Worker wrapper for <see cref="IDraftInviteCalendar"/> (M15.2, SPEC 22.3).</summary>
internal sealed class OutlookDraftInviteCalendar(OutlookWorker worker, ILogger? log = null) : IDraftInviteCalendar
{
    public Task<IReadOnlyList<DraftInviteOutcome>> CheckAsync(IReadOnlyList<DraftInviteEntry> entries,
        Func<DraftInviteSnapshot, DraftInviteDecision> decide, WorkPriority priority, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.CheckDraftInvites(app, entries, decide, log), priority, ct);
}

internal static partial class OutlookCalendar
{
    private sealed record SharedCalendar(Outlook.MAPIFolder? Folder);

    /// <summary>Worker thread. Writes only Categories + Save() on RemoveCategory decisions (SPEC 22.2).</summary>
    public static IReadOnlyList<DraftInviteOutcome> CheckDraftInvites(Outlook.Application app,
        IReadOnlyList<DraftInviteEntry> entries, Func<DraftInviteSnapshot, DraftInviteDecision> decide, ILogger? log)
    {
        var results = new List<DraftInviteOutcome>(entries.Count);
        var shared = new Dictionary<string, SharedCalendar>(StringComparer.OrdinalIgnoreCase);
        Outlook.NameSpace? ns = null;
        try
        {
            ns = app.Session;
            var open = OpenInspectorEntryIds(app);
            foreach (var entry in entries)
            {
                SharedCalendar? cal = null;
                if (entry.Mailbox.Length > 0)
                {
                    if (!shared.TryGetValue(entry.Mailbox, out cal))
                    {
                        cal = new SharedCalendar(OpenSharedCalendar(ns, entry.Mailbox, log));
                        shared[entry.Mailbox] = cal;
                    }
                }
                results.Add(CheckOneDraftInvite(ns, entry, cal, open, decide, log));
            }
            return results;
        }
        finally
        {
            foreach (var c in shared.Values) ReleaseCom(c.Folder);
            ReleaseCom(ns);
        }
    }

    private static void ReleaseCom(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    private static HashSet<string> OpenInspectorEntryIds(Outlook.Application app)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Outlook.Inspectors? insps = null;
        try
        {
            insps = app.Inspectors;
            for (var i = 1; i <= insps.Count; i++)
            {
                Outlook.Inspector? insp = null;
                object? cur = null;
                try
                {
                    insp = insps[i];
                    cur = insp.CurrentItem;
                    if (cur is Outlook.AppointmentItem a)
                    {
                        var id = ComSafe.Try(() => a.EntryID);
                        if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                    }
                }
                catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
                {
                    // One inspector that can't be read is skipped.
                }
                finally
                {
                    ReleaseCom(cur);
                    ReleaseCom(insp);
                }
            }
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            // No inspector list: nothing is treated as open.
        }
        finally
        {
            ReleaseCom(insps);
        }
        return ids;
    }

    /// <summary>Opens the shared mailbox's calendar so its store is open in the session. Null when that fails.</summary>
    private static Outlook.MAPIFolder? OpenSharedCalendar(Outlook.NameSpace ns, string mailbox, ILogger? log)
    {
        Outlook.Recipient? owner = null;
        try
        {
            owner = ns.CreateRecipient(mailbox);
            owner.Resolve();
            return ns.GetSharedDefaultFolder(owner, Outlook.OlDefaultFolders.olFolderCalendar);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException c && !ComErrors.IsOutlookGone(c)))
        {
            log?.LogWarning("Opening a shared calendar for the draft invite check failed ({Type}).", ex.GetType().Name);
            return null;
        }
        finally
        {
            ReleaseCom(owner);
        }
    }

    private static DraftInviteOutcome CheckOneDraftInvite(Outlook.NameSpace ns, DraftInviteEntry entry, SharedCalendar? cal,
        HashSet<string> openEntryIds, Func<DraftInviteSnapshot, DraftInviteDecision> decide, ILogger? log)
    {
        if (cal is not null && cal.Folder is null)
        {
            var unreadable = new DraftInviteSnapshot(DraftInviteLookup.Unreadable);
            return new DraftInviteOutcome(entry, decide(unreadable), null, false);
        }

        object? obj = null;
        try
        {
            try
            {
                var storeId = entry.StoreId;
                if (storeId.Length == 0 && cal?.Folder is { } f) storeId = ComSafe.Try(() => f.StoreID) ?? "";
                object store = storeId.Length > 0 ? storeId : Type.Missing;
                obj = ns.GetItemFromID(entry.EntryId, store);
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                var lookup = ComErrors.IsItemNotFound(ex) ? DraftInviteLookup.NotFound : DraftInviteLookup.Unreadable;
                return new DraftInviteOutcome(entry, decide(new DraftInviteSnapshot(lookup)), null, false);
            }

            if (obj is not Outlook.AppointmentItem item)
            {
                return new DraftInviteOutcome(entry, decide(new DraftInviteSnapshot(DraftInviteLookup.NotFound)), null, false);
            }

            int status;
            string categories;
            try
            {
                status = (int)item.MeetingStatus;
                categories = item.Categories ?? "";
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                return new DraftInviteOutcome(entry, decide(new DraftInviteSnapshot(DraftInviteLookup.Unreadable)), null, false);
            }

            var inDeleted = IsInDeletedItems(item);
            var invited = ReadInvited(item);
            DraftInviteDetails? details = null;
            try
            {
                details = new DraftInviteDetails(item.Subject ?? "",
                    DateTime.SpecifyKind(item.Start, DateTimeKind.Unspecified),
                    DateTime.SpecifyKind(item.End, DateTimeKind.Unspecified));
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                // Details stay unknown.
            }
            var id = ComSafe.Try(() => item.EntryID);
            var snapshot = new DraftInviteSnapshot(DraftInviteLookup.Found, inDeleted, status, invited, categories, details,
                !string.IsNullOrWhiteSpace(id) && openEntryIds.Contains(id));

            var d = decide(snapshot);
            var applied = false;
            if (d.Action == DraftInviteAction.RemoveCategory)
            {
                try
                {
                    item.Categories = d.NewCategories!;
                    item.Save();
                    applied = true;
                }
                catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
                {
                    log?.LogWarning("Removing the draft-invite category failed ({Type}); retrying next check.", ex.GetType().Name);
                }
            }
            return new DraftInviteOutcome(entry, d, details, applied);
        }
        finally
        {
            ReleaseCom(obj);
        }
    }

    private static bool IsInDeletedItems(Outlook.AppointmentItem item)
    {
        Outlook.MAPIFolder? parent = null;
        Outlook.Store? store = null;
        Outlook.MAPIFolder? del = null;
        try
        {
            parent = item.Parent as Outlook.MAPIFolder;
            if (parent is null) return false;
            store = parent.Store;
            del = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDeletedItems);
            if (string.Equals(parent.EntryID, del.EntryID, StringComparison.OrdinalIgnoreCase)) return true;
            return (parent.FolderPath ?? "").StartsWith((del.FolderPath ?? "") + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            // A shared mailbox may not expose its Deleted Items.
            return false;
        }
        finally
        {
            ReleaseCom(del);
            ReleaseCom(store);
            ReleaseCom(parent);
        }
    }

    private static bool? ReadInvited(Outlook.AppointmentItem item)
    {
        var pa = ComSafe.Try(() => item.PropertyAccessor);
        try
        {
            if (pa is null) return null;
            return ComSafe.Try(() => (object?)pa.GetProperty(DraftInvites.FInvitedProperty)) is bool b ? b : null;
        }
        finally
        {
            ReleaseCom(pa);
        }
    }
}
