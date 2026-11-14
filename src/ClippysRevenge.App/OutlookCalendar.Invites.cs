using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Worker wrappers for <see cref="IMeetingCalendar"/> (M8.4).</summary>
internal sealed class OutlookMeetingCalendar(OutlookWorker worker, OutlookStores stores, Func<Settings> settings,
    ILogger? log = null) : IMeetingCalendar
{
    public async Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct)
    {
        if (request.Mailbox.Length > 0)
            await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        return await worker.RunAsync(app => OutlookCalendar.ResolveInvite(app, stores, settings(), request), WorkPriority.Interactive, ct)
            .ConfigureAwait(false);
    }

    public Task<CreatedMeeting> CreateAsync(MeetingRequest request, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.CreateMeeting(app, request, log), WorkPriority.Interactive, ct);
}

internal static partial class OutlookCalendar
{
    private const int OlRequired = 1; // OlMeetingRecipientType.olRequired

    /// <summary>Validates the mailbox and resolves the organizer and every recipient. Creates nothing. Worker thread.</summary>
    public static ResolvedInvite ResolveInvite(Outlook.Application app, OutlookStores stores, Settings settings, MeetingRequest req)
    {
        Marshal.ReleaseComObject(stores.Get(app, req.Mailbox));

        var session = app.Session;
        try
        {
            string orgName, orgEmail;
            if (req.Mailbox.Length == 0)
            {
                (orgName, orgEmail) = ReadCurrentUser(session);
            }
            else
            {
                var sm = settings.FindSharedMailbox(req.Mailbox);
                orgName = string.IsNullOrWhiteSpace(sm?.DisplayName) ? req.Mailbox : sm!.DisplayName;
                orgEmail = req.Mailbox;
            }

            var attendees = new List<InviteAttendee>();
            var unresolved = new List<string>();
            foreach (var r in req.Recipients)
            {
                Outlook.Recipient? rc = null;
                Outlook.AddressEntry? entry = null;
                Outlook.ExchangeUser? user = null;
                try
                {
                    rc = session.CreateRecipient(r.Email);
                    if (!rc.Resolve())
                    {
                        unresolved.Add(Recipients.Format([r]));
                        continue;
                    }
                    entry = rc.AddressEntry;
                    user = entry.GetExchangeUser();
                    var name = string.IsNullOrWhiteSpace(entry.Name) ? r.Name : entry.Name;
                    var email = string.IsNullOrWhiteSpace(user?.PrimarySmtpAddress) ? r.Email : user!.PrimarySmtpAddress;
                    attendees.Add(new InviteAttendee(name, email, !AttendeeKinds.IsInternal((int)entry.AddressEntryUserType)));
                }
                finally
                {
                    if (user is not null) Marshal.ReleaseComObject(user);
                    if (entry is not null) Marshal.ReleaseComObject(entry);
                    if (rc is not null) Marshal.ReleaseComObject(rc);
                }
            }
            return new ResolvedInvite(orgName, orgEmail, attendees, unresolved);
        }
        finally
        {
            Marshal.ReleaseComObject(session);
        }
    }

    private static (string Name, string Email) ReadCurrentUser(Outlook.NameSpace session)
    {
        Outlook.Recipient? me = null;
        Outlook.AddressEntry? entry = null;
        Outlook.ExchangeUser? user = null;
        try
        {
            me = session.CurrentUser;
            entry = me.AddressEntry;
            user = entry.GetExchangeUser();
            var email = string.IsNullOrWhiteSpace(user?.PrimarySmtpAddress) ? entry.Address : user!.PrimarySmtpAddress;
            return (entry.Name ?? "", email ?? "");
        }
        finally
        {
            if (user is not null) Marshal.ReleaseComObject(user);
            if (entry is not null) Marshal.ReleaseComObject(entry);
            if (me is not null) Marshal.ReleaseComObject(me);
        }
    }

    private static bool IsAccessFailure(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException c && !ComErrors.IsOutlookGone(c));

    /// <summary>Creates the meeting and saves it unsent. Worker thread. Never sends (SPEC §1.4, §22).</summary>
    public static CreatedMeeting CreateMeeting(Outlook.Application app, MeetingRequest req, ILogger? log = null)
    {
        var shared = req.Mailbox.Length > 0;
        Outlook.NameSpace? session = null;
        Outlook.Recipient? owner = null;
        Outlook.MAPIFolder? folder = null;
        Outlook.Items? items = null;
        Outlook.AppointmentItem? item = null;
        Outlook.Recipients? recips = null;
        var categorized = false;
        try
        {
            try
            {
                if (shared)
                {
                    session = app.Session;
                    owner = session.CreateRecipient(req.Mailbox);
                    owner.Resolve();
                    folder = session.GetSharedDefaultFolder(owner, Outlook.OlDefaultFolders.olFolderCalendar);
                    items = folder.Items;
                    item = (Outlook.AppointmentItem)items.Add(Outlook.OlItemType.olAppointmentItem);
                }
                else
                {
                    item = (Outlook.AppointmentItem)app.CreateItem(Outlook.OlItemType.olAppointmentItem);
                }

                item.MeetingStatus = Outlook.OlMeetingStatus.olMeeting;
                item.Subject = req.Subject;
                item.Start = req.Start;
                item.End = req.End;
                if (req.Location is not null) item.Location = req.Location;
                if (req.Body.Length > 0) item.Body = req.Body;

                recips = item.Recipients;
                foreach (var r in req.Recipients)
                {
                    var rc = recips.Add(r.Email);
                    try
                    {
                        rc.Type = OlRequired;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(rc);
                    }
                }
                if (!recips.ResolveAll())
                {
                    var bad = new List<string>();
                    for (var i = 1; i <= recips.Count; i++)
                    {
                        var rc = recips[i];
                        try
                        {
                            if (!rc.Resolved) bad.Add(rc.Name);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(rc);
                        }
                    }
                    item.Close(Outlook.OlInspectorClose.olDiscard);
                    throw new McpException(InviteTexts.Unresolved(bad));
                }

                EnsureDraftCategory(app, log);
                categorized = TrySetDraftCategory(item, log);
                item.Save();
            }
            catch (Exception ex) when (shared && IsAccessFailure(ex))
            {
                throw new McpException(InviteTexts.NoCalendarRights(req.Mailbox));
            }

            // Read the ids in their own guard: a failure after a successful save is not "no calendar rights".
            string entryId = "", storeId = "";
            object? parent = null;
            try
            {
                entryId = item.EntryID ?? "";
                if (folder is not null)
                {
                    storeId = folder.StoreID ?? "";
                }
                else
                {
                    parent = item.Parent;
                    if (parent is Outlook.MAPIFolder pf) storeId = pf.StoreID ?? "";
                }
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                // Saved; ids unknown.
            }
            finally
            {
                if (parent is not null && Marshal.IsComObject(parent)) Marshal.ReleaseComObject(parent);
            }
            return new CreatedMeeting(entryId, storeId, categorized);
        }
        finally
        {
            if (recips is not null) Marshal.ReleaseComObject(recips);
            if (item is not null) Marshal.ReleaseComObject(item);
            if (items is not null) Marshal.ReleaseComObject(items);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            if (owner is not null) Marshal.ReleaseComObject(owner);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    private static volatile bool s_draftCategoryKnown;

    /// <summary>Makes sure Clippy's category is in the own master category list (orange). Never recolours or renames
    /// an existing one. Worker thread. Outlook-gone errors propagate.</summary>
    private static void EnsureDraftCategory(Outlook.Application app, ILogger? log)
    {
        if (s_draftCategoryKnown) return;
        Outlook.NameSpace? session = null;
        Outlook.Categories? cats = null;
        try
        {
            session = app.Session;
            cats = session.Categories;
            var found = false;
            for (var i = 1; i <= cats.Count && !found; i++)
            {
                var c = cats[i];
                try
                {
                    found = string.Equals(c.Name, DraftInvites.CategoryName, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Marshal.ReleaseComObject(c);
                }
            }
            if (!found)
            {
                var added = cats.Add(DraftInvites.CategoryName, Outlook.OlCategoryColor.olCategoryColorOrange);
                if (added is not null) Marshal.ReleaseComObject(added);
            }
            s_draftCategoryKnown = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException c && !ComErrors.IsOutlookGone(c)))
        {
            log?.LogWarning("Adding the draft-invite category to the master category list failed ({Type}).", ex.GetType().Name);
        }
        finally
        {
            if (cats is not null) Marshal.ReleaseComObject(cats);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    private static bool TrySetDraftCategory(Outlook.AppointmentItem item, ILogger? log)
    {
        try
        {
            item.Categories = DraftInvites.CategoryName;
            return true;
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            log?.LogWarning("Setting the draft-invite category failed ({Type}).", ex.GetType().Name);
            return false;
        }
    }}
