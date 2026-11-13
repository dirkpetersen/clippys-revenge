using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

internal static partial class OutlookCalendar
{
    private const int OlFolderCalendar = 9;

    /// <summary>
    /// Events overlapping the window, at most <see cref="CalendarEvents.MaxEvents"/>. IncludeRecurrences is set
    /// BEFORE Sort and Restrict (SPEC §11.7). Runs on the worker thread. Releases every COM object.
    /// </summary>
    public static IReadOnlyList<CalendarEventInfo> ListEvents(
        Outlook.Application app, OutlookStores stores, string? mailbox, EventWindow window, TimeZoneInfo tz)
    {
        var key = EmailAddress.Normalize(mailbox);
        Outlook.Folder? folder = null;
        Outlook.Items? items = null;
        Outlook.Items? restricted = null;
        try
        {
            folder = OpenCalendar(app, stores, key);
            items = folder.Items;
            items.IncludeRecurrences = true;
            items.Sort("[Start]");
            restricted = items.Restrict(CalendarEvents.BuildFilter(window, tz));

            var result = new List<CalendarEventInfo>();
            // Never use Count: an open-ended recurrence makes it huge. Walk and stop at the cap.
            var item = restricted.GetFirst();
            while (item is not null && result.Count < CalendarEvents.MaxEvents)
            {
                try
                {
                    if (item is Outlook.AppointmentItem a) result.Add(Read(a, tz));
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
                item = restricted.GetNext();
            }
            if (item is not null) Marshal.ReleaseComObject(item);
            return result;
        }
        finally
        {
            if (restricted is not null) Marshal.ReleaseComObject(restricted);
            if (items is not null) Marshal.ReleaseComObject(items);
            if (folder is not null) Marshal.ReleaseComObject(folder);
        }
    }

    private static Outlook.Folder OpenCalendar(Outlook.Application app, OutlookStores stores, string key)
    {
        var session = app.Session;
        try
        {
            if (key.Length == 0) return (Outlook.Folder)session.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderCalendar);

            // Validates the address against the configured mailboxes (throws McpException).
            Marshal.ReleaseComObject(stores.Get(app, key));
            Outlook.Recipient? owner = null;
            try
            {
                owner = session.CreateRecipient(key);
                if (!owner.Resolve())
                    throw new McpException($"Could not resolve \"{key}\" in the address book.");
                return (Outlook.Folder)session.GetSharedDefaultFolder(owner, Outlook.OlDefaultFolders.olFolderCalendar);
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                throw new McpException(
                    $"The calendar of \"{key}\" cannot be opened. The user needs at least reviewer permission on that mailbox's Calendar folder.");
            }
            finally
            {
                if (owner is not null) Marshal.ReleaseComObject(owner);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(session);
        }
    }

    private static CalendarEventInfo Read(Outlook.AppointmentItem a, TimeZoneInfo tz) => new(
        a.EntryID ?? "",
        a.Subject ?? "",
        CalendarEvents.FromLocal(a.Start, tz),
        CalendarEvents.FromLocal(a.End, tz),
        a.AllDayEvent,
        a.Location ?? "",
        a.Organizer ?? "",
        a.IsRecurring,
        CalendarEvents.BusyStatusName((int)a.BusyStatus));
}
