using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

internal sealed record DraftFacts(string Subject, bool Sent, string ParentId, string ParentStoreId, string DraftsId,
    int RecipientCount, string Html, string Text, IReadOnlyList<string> AttachmentNames, string OnBehalf,
    string? ClippyDraft);

/// <summary>Reads a saved draft back from Outlook (shared by the live draft tests). Worker thread only.</summary>
internal static class LiveDrafts
{
    internal static void Rel(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    /// <summary>
    /// Reads the draft. With <paramref name="tag"/>, then prefixes its subject with "[RunToken] " and saves the draft,
    /// so that cleanup (which only deletes token-subject items) can find a reply draft, whose subject is the original's.
    /// The returned facts are those from BEFORE tagging.
    /// </summary>
    internal static DraftFacts ReadDraft(LiveOutlook fx, Outlook.Application app, string mailbox, string id, bool tag = false)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? item = null;
        Outlook.MAPIFolder? parent = null;
        Outlook.Recipients? recips = null;
        Outlook.Attachments? atts = null;
        try
        {
            session = app.Session;
            item = (Outlook.MailItem)session.GetItemFromID(id, fx.StoreIdOf(app, mailbox));
            parent = (Outlook.MAPIFolder)item.Parent;
            recips = item.Recipients;
            atts = item.Attachments;
            var names = new List<string>();
            for (var i = 1; i <= atts.Count; i++)
            {
                Outlook.Attachment? a = null;
                try { a = atts[i]; names.Add(a.FileName); }
                finally { Rel(a); }
            }
            var clippyDraft = ReadMarker(item);
            var facts = new DraftFacts(item.Subject ?? "", item.Sent, parent.EntryID, parent.StoreID,
                fx.DefaultFolderId(app, mailbox, Outlook.OlDefaultFolders.olFolderDrafts),
                recips.Count, item.HTMLBody ?? "", item.Body ?? "", names, item.SentOnBehalfOfName ?? "", clippyDraft);
            if (tag && !facts.Sent)
            {
                item.Subject = $"[{fx.RunToken}] {facts.Subject}";
                item.Save();
            }
            return facts;
        }
        finally
        {
            Rel(atts);
            Rel(recips);
            Rel(parent);
            Rel(item);
            Rel(session);
        }
    }

    /// <summary>Outlook's own reply to an item, in memory only (closed with olDiscard, never saved). Worker thread only.
    /// (null, HResult) when Outlook can't reply to it (non-"Outlook gone" COMException).</summary>
    internal static (string? Html, int HResult) TryOwnReply(Outlook.Application app, string id, string storeId)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? item = null;
        Outlook.MailItem? reply = null;
        try
        {
            session = app.Session;
            item = (Outlook.MailItem)session.GetItemFromID(id, storeId);
            reply = item.Reply();
            var html = reply.HTMLBody ?? "";
            try { reply.Close(Outlook.OlInspectorClose.olDiscard); }
            catch (COMException) { /* never opened in a window; nothing to close */ }
            return (html, 0);
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return (null, ex.HResult);
        }
        finally
        {
            Rel(reply);
            Rel(item);
            Rel(session);
        }
    }

    /// <summary>The ClippyDraft property; a COMException (not set) gives null.</summary>
    internal static string? ReadMarker(Outlook.MailItem item)
    {
        Outlook.PropertyAccessor? pa = null;
        try
        {
            pa = item.PropertyAccessor;
            return pa.GetProperty(DraftMarker.PropertyName) as string;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Rel(pa);
        }
    }
}
