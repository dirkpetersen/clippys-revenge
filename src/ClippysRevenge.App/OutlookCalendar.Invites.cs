using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Worker wrappers for <see cref="IMeetingCalendar"/> (M8.4).</summary>
internal sealed class OutlookMeetingCalendar(OutlookWorker worker, OutlookStores stores, Func<Settings> settings) : IMeetingCalendar
{
    public Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.ResolveInvite(app, stores, settings(), request), WorkPriority.Interactive, ct);

    public Task<CreatedMeeting> CreateAsync(MeetingRequest request, InviteDecision decision, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.CreateMeeting(app, request, decision), WorkPriority.Interactive, ct);
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
            return new ResolvedInvite(orgName, orgEmail, attendees, unresolved, TeamsAddinUsable(app));
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

    // M8.4 brief, Teams option A: the Outlook object model has no supported call to add a Teams meeting,
    // so v1 never adds one. Kept as a seam in case an add-in route is confirmed later.
    private static bool TeamsAddinUsable(Outlook.Application app) => false;

    /// <summary>
    /// Creates the meeting and either sends or saves it, per the decision. Worker thread. The only place in
    /// the codebase that sends anything: it runs after InviteGate and the user's confirmation.
    /// </summary>
    public static CreatedMeeting CreateMeeting(Outlook.Application app, MeetingRequest req, InviteDecision decision)
    {
        if (decision.Action is not (InviteAction.Send or InviteAction.SaveDraft))
            throw new InvalidOperationException("CreateMeeting needs a Send or SaveDraft decision.");

        var shared = req.Mailbox.Length > 0;
        Outlook.NameSpace? session = null;
        Outlook.Recipient? owner = null;
        Outlook.MAPIFolder? folder = null;
        Outlook.Items? items = null;
        Outlook.AppointmentItem? item = null;
        Outlook.Recipients? recips = null;
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

                switch (decision.Action)
                {
                    case InviteAction.Send:
                        item.Send();
                        break;
                    case InviteAction.SaveDraft:
                        item.Save();
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected invite action.");
                }
            }
            catch (Exception ex) when (shared && IsAccessFailure(ex))
            {
                throw new McpException(InviteTexts.NoCalendarRights(req.Mailbox));
            }
            return new CreatedMeeting(false);
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
}
