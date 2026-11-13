using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

internal static partial class OutlookCalendar
{
    /// <summary>SPEC §8. Runs on the worker thread only.</summary>
    public static string? GetFreeBusy(Outlook.Application app, string email, DateTime from, DateTime to, int intervalMinutes)
    {
        Outlook.NameSpace? session = null;
        Outlook.Recipient? recipient = null;
        Outlook.AddressEntry? entry = null;
        try
        {
            session = app.Session;
            recipient = session.CreateRecipient(email);
            recipient.Resolve();
            if (!recipient.Resolved) return null;
            entry = recipient.AddressEntry;
            var ae = entry;
            return FreeBusy.FetchChunked(from, to, intervalMinutes, s => ae.GetFreeBusy(s, intervalMinutes, true));
        }
        catch (COMException ex)
        {
            if (ComErrors.IsOutlookGone(ex)) throw;
            return null;
        }
        finally
        {
            if (entry is not null) Marshal.ReleaseComObject(entry);
            if (recipient is not null) Marshal.ReleaseComObject(recipient);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    /// <summary>Name + primary SMTP of Session.CurrentUser.</summary>
    public static ClippysRevenge.Core.Recipient GetCurrentUser(Outlook.Application app)
    {
        Outlook.NameSpace? session = null;
        Outlook.Recipient? user = null;
        Outlook.AddressEntry? entry = null;
        Outlook.ExchangeUser? exUser = null;
        try
        {
            session = app.Session;
            user = session.CurrentUser;
            var name = user.Name ?? "";
            entry = user.AddressEntry;
            exUser = entry.GetExchangeUser();
            var email = exUser?.PrimarySmtpAddress;
            if (string.IsNullOrWhiteSpace(email)) email = entry.Address;
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                throw new McpException(
                    "Could not read your own email address from Outlook, so include_me can't be used. Add your address to recipients and set include_me=false.");
            return new ClippysRevenge.Core.Recipient(name, email.Trim());
        }
        finally
        {
            if (exUser is not null) Marshal.ReleaseComObject(exUser);
            if (entry is not null) Marshal.ReleaseComObject(entry);
            if (user is not null) Marshal.ReleaseComObject(user);
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }
}
