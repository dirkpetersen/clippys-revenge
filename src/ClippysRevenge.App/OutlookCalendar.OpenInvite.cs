using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Worker wrapper for <see cref="IDraftInviteWindow"/> (M15.4, SPEC 22.3).</summary>
internal sealed class OutlookDraftInviteWindow(OutlookWorker worker, ILogger? log = null) : IDraftInviteWindow
{
    public Task<DraftInviteOpenResult> OpenAsync(DraftInviteEntry entry, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.OpenDraftInvite(app, entry.EntryId, entry.StoreId, entry.Mailbox, log),
            WorkPriority.Interactive, ct);
}

internal static partial class OutlookCalendar
{
    /// <summary>Worker thread. Looks the meeting up and shows it non-modally (SPEC 22.3). The only Display call in src/.
    /// Writes nothing.</summary>
    /// <exception cref="ArgumentException">entryId is null or blank.</exception>
    /// <exception cref="COMException">non-"not found" COM failures, unchanged (the worker maps Outlook-gone ones).</exception>
    /// <exception cref="UnauthorizedAccessException">the shared calendar can't be opened.</exception>
    public static DraftInviteOpenResult OpenDraftInvite(Outlook.Application app, string entryId, string storeId,
        string mailbox, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        storeId ??= "";
        mailbox ??= "";

        Outlook.NameSpace? ns = null;
        Outlook.MAPIFolder? folder = null;
        object? obj = null;
        try
        {
            ns = app.Session;
            if (mailbox.Length > 0)
            {
                // Makes sure the shared store is open after an Outlook restart, as the check does.
                folder = OpenSharedCalendar(ns, mailbox, log)
                    ?? throw new UnauthorizedAccessException("The shared calendar could not be opened.");
                if (storeId.Length == 0) storeId = ComSafe.Try(() => folder.StoreID) ?? "";
            }

            try
            {
                obj = ns.GetItemFromID(entryId, storeId.Length > 0 ? storeId : Type.Missing);
            }
            catch (COMException ex) when (ComErrors.IsItemNotFound(ex))
            {
                log?.LogDebug("OpenDraftInvite: {Outcome}", "NotFound");
                return DraftInviteOpenResult.Gone;
            }

            if (obj is not Outlook.AppointmentItem item)
            {
                log?.LogDebug("OpenDraftInvite: {Outcome}", "NotAnAppointment");
                return DraftInviteOpenResult.Gone;
            }
            if (IsInDeletedItems(item))
            {
                log?.LogDebug("OpenDraftInvite: {Outcome}", "Deleted");
                return DraftInviteOpenResult.Gone;
            }

            item.Display(false);
            Outlook.Inspector? insp = null;
            try
            {
                insp = item.GetInspector;
                insp.Activate();
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                // The window is open; only bringing it to the front failed.
            }
            finally
            {
                ReleaseCom(insp);
            }
            log?.LogDebug("OpenDraftInvite: {Outcome}", "Opened");
            return DraftInviteOpenResult.Opened;
        }
        finally
        {
            ReleaseCom(obj);
            ReleaseCom(folder);
            ReleaseCom(ns);
        }
    }
}
