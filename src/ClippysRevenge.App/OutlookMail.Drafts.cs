using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Draft creation (SPEC section 11.5). Worker thread only. Drafts are only ever saved.</summary>
internal sealed partial class OutlookMail
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    internal static CreatedDraft CreateDraft(Outlook.Application app, Outlook.Store? store, DraftPlan plan)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(plan);
        if (DraftMarker.Normalize(plan.Marker) != plan.Marker)
            throw new ArgumentException("Unknown draft marker.", nameof(plan));

        Outlook.MailItem? item = null;
        var saved = false;
        var markerSet = false;
        var stage = 0; // 1 = creating, 2 = attaching
        string? attachName = null;
        try
        {
            stage = 1;
            if (store is null)
            {
                item = (Outlook.MailItem)app.CreateItem(Outlook.OlItemType.olMailItem);
            }
            else
            {
                Outlook.MAPIFolder? drafts = null;
                Outlook.Items? items = null;
                try
                {
                    drafts = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDrafts);
                    items = drafts.Items;
                    item = (Outlook.MailItem)items.Add(Outlook.OlItemType.olMailItem);
                }
                finally
                {
                    Release(items);
                    Release(drafts);
                }
                item.SentOnBehalfOfName = plan.Mailbox;
            }
            stage = 0;

            markerSet = TrySetDraftMarker(item, plan.Marker);

            item.Subject = plan.Subject;

            Outlook.Recipients? recips = item.Recipients;
            try
            {
                AddRecipients(recips, plan.To, Outlook.OlMailRecipientType.olTo);
                AddRecipients(recips, plan.Cc, Outlook.OlMailRecipientType.olCC);
                AddRecipients(recips, plan.Bcc, Outlook.OlMailRecipientType.olBCC);
            }
            finally
            {
                Release(recips);
            }

            item.BodyFormat = Outlook.OlBodyFormat.olFormatHTML;
            item.HTMLBody = HtmlBody.InsertAtTop(item.HTMLBody ?? "", plan.HtmlFragment);

            stage = 2;
            Outlook.Attachments? atts = item.Attachments;
            try
            {
                foreach (var path in plan.AttachmentPaths)
                {
                    attachName = Path.GetFileName(path);
                    Outlook.Attachment? a = null;
                    try
                    {
                        a = atts.Add(path, Outlook.OlAttachmentType.olByValue);
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
            stage = 0;

            item.Save();
            saved = true;
            try
            {
                return new CreatedDraft(item.EntryID, item.Subject ?? "", markerSet);
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                throw new McpException("The draft was saved in Outlook, but its id could not be read. Look in the Drafts folder.");
            }
        }
        catch (Exception ex) when (!saved)
        {
            Discard(item);
            if (ex is McpException || ComErrors.IsOutlookGone(ex)) throw;
            if (ex is COMException)
            {
                if (store is not null && stage == 1 && ex.HResult == E_ACCESSDENIED)
                    throw new McpException($"Could not create a draft in {plan.Mailbox}: Outlook refused access to that mailbox's Drafts folder. The user needs Full Access or Editor rights on it.");
                if (stage == 2)
                    throw new McpException($"Outlook could not attach \"{attachName}\": {ex.Message} Nothing was created.");
                throw new McpException($"Outlook could not create the draft: {ex.Message}");
            }
            throw;
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>SetProperty(DraftMarker.PropertyName, marker). False on a COMException that isn't "Outlook gone" (which propagates).</summary>
    private static bool TrySetDraftMarker(Outlook.MailItem item, string marker)
    {
        Outlook.PropertyAccessor? pa = null;
        try
        {
            pa = item.PropertyAccessor;
            pa.SetProperty(DraftMarker.PropertyName, marker);
            return true;
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return false;
        }
        finally
        {
            Release(pa);
        }
    }

    private static void AddRecipients(Outlook.Recipients recips, IReadOnlyList<Recipient> list, Outlook.OlMailRecipientType type)
    {
        foreach (var rec in list)
        {
            Outlook.Recipient? r = null;
            try
            {
                r = recips.Add(rec.Email);
                r.Type = (int)type;
            }
            finally
            {
                Release(r);
            }
        }
    }

    private static void Discard(Outlook.MailItem? item)
    {
        if (item is null) return;
        try
        {
            item.Close(Outlook.OlInspectorClose.olDiscard);
        }
        catch
        {
            // best effort
        }
    }
}
