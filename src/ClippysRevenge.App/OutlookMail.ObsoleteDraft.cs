using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Clippy's own tracked AI drafts (SPEC 23.4, 23.5). Worker thread only. The one Move here puts an outdated,
/// unedited, unopened AI draft into its own store's Deleted Items, only when AiDraftGuard says Movable. Nothing here
/// sets a property, saves, closes or deletes.</summary>
internal sealed partial class OutlookMail
{
    /// <summary>SPEC 23.4: DraftFingerprint of Subject, To, CC, BCC, HTMLBody, Categories, (int)Importance,
    /// SentOnBehalfOfName (From), (int)Sensitivity and the attachment file names in position order. The one place it is read (at creation and at every check).</summary>
    /// <exception cref="ArgumentNullException">item is null.</exception>
    /// <exception cref="COMException">Any COM read failed (incl. Outlook gone).</exception>
    internal static string ReadDraftFingerprint(Outlook.MailItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var subject = item.Subject;
        var to = item.To;
        var cc = item.CC;
        var bcc = item.BCC;
        var html = item.HTMLBody;
        var categories = item.Categories;
        var importance = (int)item.Importance;
        var from = item.SentOnBehalfOfName;
        var sensitivity = (int)item.Sensitivity;
        var names = new List<string?>();
        Outlook.Attachments? atts = null;
        try
        {
            atts = item.Attachments;
            var count = atts.Count;
            for (var i = 1; i <= count; i++)
            {
                Outlook.Attachment? a = null;
                try
                {
                    a = atts[i];
                    var name = a.FileName;
                    names.Add(string.IsNullOrEmpty(name) ? "?" : name);
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
        return DraftFingerprint.Compute(new DraftFingerprintInput(subject, to, cc, bcc, html, categories, importance, from, sensitivity, names));
    }

    /// <summary>ReadDraftFingerprint, or null when it threw anything other than an Outlook-gone COMException
    /// (which propagates). Used right after Save() in CreateReplyDraft: a failed read never fails the draft.</summary>
    internal static string? TryReadDraftFingerprint(Outlook.MailItem item)
    {
        try
        {
            return ReadDraftFingerprint(item);
        }
        catch (Exception ex) when (!(ex is COMException c && ComErrors.IsOutlookGone(c)))
        {
            return null;
        }
    }

    /// <summary>Read-only (SPEC 23.5). Null when the item can't be found (ItemNotFound) or isn't a MailItem.</summary>
    /// <exception cref="ArgumentNullException">app, mailbox or draftEntryId is null.</exception>
    /// <exception cref="COMException">A COM error other than "not found" (incl. Outlook gone); the caller decides.</exception>
    internal AiDraftInfo? ReadAiDraft(Outlook.Application app, string mailbox, string draftEntryId)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(draftEntryId);
        var draft = OpenDraft(app, mailbox, draftEntryId);
        if (draft is null) return null;
        try
        {
            return ReadDraftInfo(app, draft);
        }
        finally
        {
            Release(draft);
        }
    }

    /// <summary>One work item (SPEC 23.5): open, read AiDraftInfo, AiDraftGuard.Evaluate, and only when Movable
    /// draft.Move(deletedItems) into the draft's own store's Deleted Items. Gone gives (Gone, "").
    /// Deleted Items can't be opened gives (NoDeletedItems, subject), nothing moved. Moved on success.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="COMException">Any other COM error, incl. a failed Move (the caller treats it as "left").</exception>
    internal ObsoleteDraftMove MoveObsoleteAiDraft(Outlook.Application app, string mailbox, string draftEntryId,
        string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(draftEntryId);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        var draft = OpenDraft(app, mailbox, draftEntryId);
        if (draft is null) return new ObsoleteDraftMove(ObsoleteDraftStatus.Gone, "");
        Outlook.MAPIFolder? parent = null;
        Outlook.Store? store = null;
        Outlook.MAPIFolder? deletedItems = null;
        object? moved = null;
        try
        {
            var info = ReadDraftInfo(app, draft);
            var status = AiDraftGuard.Evaluate(info, expectedFingerprint);
            if (status == ObsoleteDraftStatus.Movable)
            {
                parent = (Outlook.MAPIFolder)draft.Parent;
                store = parent.Store;
                deletedItems = TryDeletedItems(store);
                if (deletedItems is null) return new ObsoleteDraftMove(ObsoleteDraftStatus.NoDeletedItems, info.Subject);
                moved = draft.Move(deletedItems);
                return new ObsoleteDraftMove(ObsoleteDraftStatus.Moved, info.Subject);
            }
            return new ObsoleteDraftMove(status, info.Subject);
        }
        finally
        {
            Release(moved);
            Release(deletedItems);
            Release(store);
            Release(parent);
            Release(draft);
        }
    }

    /// <summary>The store's Deleted Items folder, or null. Also used read-only to canonicalize the localized name (SPEC 9); the only place olFolderDeletedItems is named besides the draft-invite cleanup.</summary>
    internal static Outlook.MAPIFolder? TryDeletedItems(Outlook.Store store)
    {
        try
        {
            return store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDeletedItems);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException c && !ComErrors.IsOutlookGone(c)))
        {
            return null;
        }
    }

    private Outlook.MailItem? OpenDraft(Outlook.Application app, string mailbox, string draftEntryId)
    {
        var storeId = StoreIdOf(_stores, app, mailbox);
        Outlook.NameSpace? ns = null;
        object? obj = null;
        try
        {
            ns = app.Session;
            obj = ns.GetItemFromID(draftEntryId, storeId ?? (object)Type.Missing);
        }
        catch (COMException ex) when (ComErrors.IsItemNotFound(ex))
        {
            return null;
        }
        finally
        {
            Release(ns);
        }
        if (obj is Outlook.MailItem mail) return mail;
        Release(obj);
        return null;
    }

    private AiDraftInfo ReadDraftInfo(Outlook.Application app, Outlook.MailItem draft)
    {
        var messageClass = draft.MessageClass;
        var sent = draft.Sent;
        var subject = draft.Subject ?? "";
        var entryId = draft.EntryID;
        var marker = ReadMarker(draft);
        var inDrafts = IsInDrafts(draft);
        var open = IsOpenInOutlook(app, entryId);
        var fingerprint = ReadDraftFingerprint(draft);
        string plainBody;
        try
        {
            plainBody = draft.Body ?? "";
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            plainBody = "";
        }
        if (plainBody.Length > AiDraftInfo.MaxPlainBodyChars) plainBody = plainBody[..AiDraftInfo.MaxPlainBodyChars];
        return new AiDraftInfo(inDrafts, sent, marker, messageClass, open, fingerprint, subject, plainBody);
    }

    private static string? ReadMarker(Outlook.MailItem draft)
    {
        Outlook.PropertyAccessor? pa = null;
        try
        {
            pa = draft.PropertyAccessor;
            return pa.GetProperty(DraftMarker.PropertyName) as string;
        }
        catch (COMException ex) when (!ComErrors.IsOutlookGone(ex))
        {
            return null;
        }
        finally
        {
            Release(pa);
        }
    }

    private static bool IsInDrafts(Outlook.MailItem draft)
    {
        Outlook.MAPIFolder? parent = null;
        Outlook.Store? store = null;
        Outlook.MAPIFolder? drafts = null;
        try
        {
            parent = draft.Parent as Outlook.MAPIFolder;
            if (parent is null) return false;
            store = parent.Store;
            drafts = store.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderDrafts);
            return string.Equals(parent.EntryID, drafts.EntryID, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is COMException c && !ComErrors.IsOutlookGone(c)))
        {
            return false;
        }
        finally
        {
            Release(drafts);
            Release(store);
            Release(parent);
        }
    }

    /// <summary>SPEC 23.5 Open: true when any Inspector or inline response shows this item. Fail-safe: a failed check counts as open.</summary>
    private static bool IsOpenInOutlook(Outlook.Application app, string entryId)
    {
        Outlook.Inspectors? insps = null;
        Outlook.Explorers? exps = null;
        try
        {
            insps = app.Inspectors;
            var ic = insps.Count;
            for (var i = 1; i <= ic; i++)
            {
                Outlook.Inspector? insp = null;
                object? cur = null;
                try
                {
                    insp = insps[i];
                    cur = insp.CurrentItem;
                    if (cur is Outlook.MailItem m && SameEntryId(m, entryId)) return true;
                }
                finally
                {
                    Release(cur);
                    Release(insp);
                }
            }
            exps = app.Explorers;
            var ec = exps.Count;
            for (var i = 1; i <= ec; i++)
            {
                Outlook.Explorer? exp = null;
                object? inline = null;
                try
                {
                    exp = exps[i];
                    inline = exp.ActiveInlineResponse;
                    if (inline is Outlook.MailItem m && SameEntryId(m, entryId)) return true;
                }
                finally
                {
                    Release(inline);
                    Release(exp);
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is InvalidCastException
            || (ex is COMException c && !ComErrors.IsOutlookGone(c)))
        {
            return true;
        }
        finally
        {
            Release(exps);
            Release(insps);
        }
    }

    private static bool SameEntryId(Outlook.MailItem m, string entryId)
    {
        var id = ComSafe.Try(() => m.EntryID);
        return !string.IsNullOrEmpty(id) && string.Equals(id, entryId, StringComparison.OrdinalIgnoreCase);
    }
}
