using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Reply drafts (SPEC section 8 CreateReplyDraft, 11.6). Worker thread only. Drafts are only ever saved.</summary>
internal sealed partial class OutlookMail
{
    /// <summary>Worker thread. Resolves the email (SPEC section 8 chain), releases it, returns its mailbox key. Throws ItemNotFoundException.</summary>
    internal string CheckReplyTarget(Outlook.Application app, string id)
    {
        var r = Resolve(app, id);
        try { return r.Mailbox; }
        finally { Release(r.Item); }
    }

    internal CreatedReply CreateReplyDraft(Outlook.Application app, ReplyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(plan);
        if (DraftMarker.Normalize(plan.Marker) != plan.Marker)
            throw new ArgumentException("Unknown draft marker.", nameof(plan));

        var resolved = Resolve(app, plan.Id);
        var original = resolved.Item;
        Outlook.MailItem? reply = null;
        Outlook.Store? store = null;
        var saved = false;
        var markerSet = false;
        try
        {
            var target = plan.Mailbox ?? resolved.Mailbox;
            if (target.Length != 0) store = _stores.Get(app, target);

            reply = plan.ReplyAll ? original.ReplyAll() : original.Reply();
            reply.Subject = ReplySubject.Normalize(original.Subject);
            reply.BodyFormat = Outlook.OlBodyFormat.olFormatHTML;
            reply.HTMLBody = HtmlBody.InsertAtTop(reply.HTMLBody ?? "", plan.HtmlFragment);

            // Named properties survive Move, so set the marker before it.
            markerSet = TrySetDraftMarker(reply, plan.Marker);

            if (store is not null)
            {
                reply.SentOnBehalfOfName = target;
                Outlook.MAPIFolder? drafts = null;
                try
                {
                    drafts = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDrafts);
                    var moved = (Outlook.MailItem)reply.Move(drafts);
                    Release(reply);
                    reply = moved;
                }
                finally
                {
                    Release(drafts);
                }
            }

            reply.Save();
            saved = true;
            try
            {
                return new CreatedReply(reply.EntryID, reply.Subject ?? "", target, markerSet);
            }
            catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
            {
                throw new McpException("The draft was saved in Outlook, but its id could not be read. Look in the Drafts folder.");
            }
        }
        catch (Exception ex) when (!saved)
        {
            Discard(reply);
            if (ex is McpException || ComErrors.IsOutlookGone(ex)) throw;
            if (ex is COMException)
                throw new McpException($"Outlook could not create the reply draft: {ex.Message}");
            throw;
        }
        finally
        {
            Release(reply);
            Release(store);
            Release(original);
        }
    }
}

internal sealed record CreatedReply(string EntryId, string Subject, string Mailbox, bool MarkerSet);
