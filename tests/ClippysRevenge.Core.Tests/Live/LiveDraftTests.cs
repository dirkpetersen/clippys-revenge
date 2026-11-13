using System.Net;
using System.Runtime.InteropServices;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>create_draft and reply_draft against real Outlook (SPEC §8, §11.5, §11.6). Drafts are saved, never sent.</summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveDraftTests(LiveOutlook fx)
{
    private sealed record DraftFacts(string Subject, bool Sent, string ParentId, string ParentStoreId, string DraftsId,
        int RecipientCount, string Html, IReadOnlyList<string> AttachmentNames, string OnBehalf);

    private Signatures Sigs() => new(SignatureRegistry.Name, Signatures.DefaultDirectory());

    private CreateDraftTool CreateTool() =>
        new(fx.Settings, Sigs(), new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub);

    private ReplyDraftTool ReplyTool() =>
        new(fx.Settings, Sigs(), new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores), fx.Hub);

    private static void Rel(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    /// <summary>
    /// Reads the draft. With <paramref name="tag"/>, then prefixes its subject with "[RunToken] " and saves the draft,
    /// so that cleanup (which only deletes token-subject items) can find a reply draft, whose subject is the original's.
    /// The returned facts are those from BEFORE tagging.
    /// </summary>
    private DraftFacts ReadDraft(Outlook.Application app, string mailbox, string id, bool tag = false)
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
            var facts = new DraftFacts(item.Subject ?? "", item.Sent, parent.EntryID, parent.StoreID,
                fx.DefaultFolderId(app, mailbox, Outlook.OlDefaultFolders.olFolderDrafts),
                recips.Count, item.HTMLBody ?? "", names, item.SentOnBehalfOfName ?? "");
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

    private static (bool Unread, DateTime Modified) OriginalState(Outlook.Application app, string id)
    {
        Outlook.NameSpace? session = null;
        Outlook.MailItem? item = null;
        try
        {
            session = app.Session;
            item = (Outlook.MailItem)session.GetItemFromID(id);
            return (item.UnRead, item.LastModificationTime);
        }
        finally
        {
            Rel(item);
            Rel(session);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Create_draft_own_mailbox_is_saved_not_sent()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var me = await fx.MeAsync();
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(fx.AllowedDir, "clippy-live.txt");
        await File.WriteAllTextAsync(file, "live test", ct);
        var subject = $"[{fx.RunToken}] draft";

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var created = await CreateTool().RunAsync(
                new CreateDraftArgs(Recipients.Format([me]), subject, "Line one\nLine <two> & three", Attachments: [file]), ct);
            var storeId = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
            scope.Track(new TrackedItem(TrackedKind.Draft, "", created.Id, storeId, subject));

            var f = await fx.RunAsync("reading the draft", app => ReadDraft(app, "", created.Id));
            Assert.Equal(subject, f.Subject);
            Assert.False(f.Sent);
            Assert.Equal(f.DraftsId, f.ParentId);
            Assert.Equal(1, f.RecipientCount);
            Assert.Contains("Line &lt;two&gt; &amp; three", f.Html);
            Assert.Equal(["clippy-live.txt"], f.AttachmentNames);
            Assert.Equal("", f.OnBehalf);
            fx.Result("signature-added", $"new={created.SignatureAdded}");
        });
    }

    [Fact(Timeout = 180_000)]
    public async Task Create_draft_with_attachment_outside_roots_creates_nothing()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var me = await fx.MeAsync();
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(fx.TempRoot, "outside");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "clippy-live.txt");
        await File.WriteAllTextAsync(file, "live test", ct);

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async _ =>
        {
            await Assert.ThrowsAnyAsync<McpException>(() => CreateTool().RunAsync(
                new CreateDraftArgs(Recipients.Format([me]), $"[{fx.RunToken}] outside", "x", Attachments: [file]), ct));
            var left = await fx.RunAsync("searching Drafts", app =>
                fx.TokenSubjectsIn(app, "", Outlook.OlDefaultFolders.olFolderDrafts));
            Assert.Empty(left);
        });
    }

    [Fact(Timeout = 180_000)]
    public async Task Reply_draft_subject_and_html_body()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        await fx.EnsureSyncedAsync();
        var hit = fx.Indexes.Search(new SearchFilter { Limit = 1 }, "").FirstOrDefault();
        if (hit is null) Assert.Skip(SkipTexts.NoData("synced Inbox mail in the last month"));
        var originalId = hit.Email.Id;
        var originalSubject = hit.Email.Subject;
        var marker = $"[{fx.RunToken}] reply text";
        var ct = TestContext.Current.CancellationToken;

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var before = await fx.RunAsync("reading the original", app => OriginalState(app, originalId));
            var reply = await ReplyTool().RunAsync(originalId, marker, false, false, null, ct);
            var storeId = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
            // A reply's subject is the original's, so it has no token: the draft is tagged right after it is read.
            scope.Track(new TrackedItem(TrackedKind.Draft, "", reply.Id, storeId, Tagged(reply.Subject)));

            var f = await fx.RunAsync("reading the reply draft", app => ReadDraft(app, "", reply.Id, tag: true));
            Assert.Equal(ReplySubject.Normalize(originalSubject), f.Subject);
            Assert.StartsWith("Re:: ", f.Subject, StringComparison.Ordinal);
            Assert.False(f.Sent);
            Assert.Equal(f.DraftsId, f.ParentId);

            var at = f.Html.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, "The reply text is not in the HTML body.");
            var quoted = string.IsNullOrEmpty(originalSubject) ? -1 : f.Html.LastIndexOf(WebUtility.HtmlEncode(originalSubject), StringComparison.Ordinal);
            if (quoted >= 0) Assert.True(at < quoted, "The reply text must come before the quoted original.");
            else Assert.True(at < f.Html.Length / 2, "The reply text must be in the first half of the body.");

            var after = await fx.RunAsync("reading the original", app => OriginalState(app, originalId));
            Assert.Equal(before.Unread, after.Unread);
            Assert.Equal(before.Modified, after.Modified);
            fx.Result("signature-added", $"reply={reply.SignatureAdded}");
        });
    }

    private string Tagged(string subject) => $"[{fx.RunToken}] {subject}";

    [Fact(Timeout = 180_000)]
    public async Task Create_draft_in_shared_mailbox()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        if (fx.SharedMailbox is not { } shared) { Assert.Skip(SkipTexts.NoShared); return; }
        var me = await fx.MeAsync();
        var ct = TestContext.Current.CancellationToken;
        await fx.Stores.EnsureResolvedAsync(WorkPriority.Interactive, ct);
        var subject = $"[{fx.RunToken}] shared draft";

        await fx.WithCleanupAsync(["", shared], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var created = await CreateTool().RunAsync(
                new CreateDraftArgs(Recipients.Format([me]), subject, "Shared mailbox draft", Mailbox: shared), ct);
            var sharedStore = await fx.RunAsync("reading the shared store id", app => fx.StoreIdOf(app, shared));
            scope.Track(new TrackedItem(TrackedKind.Draft, shared, created.Id, sharedStore, subject));

            var f = await fx.RunAsync("reading the shared draft", app => ReadDraft(app, shared, created.Id));
            Assert.Equal(sharedStore, f.ParentStoreId);
            Assert.Equal(f.DraftsId, f.ParentId);
            Assert.Equal(shared, f.OnBehalf.Trim(), ignoreCase: true);
            Assert.False(f.Sent);
        });
    }

    [Fact(Timeout = 180_000)]
    public async Task Reply_draft_moves_into_shared_drafts_before_save()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        if (fx.SharedMailbox is not { } shared) { Assert.Skip(SkipTexts.NoShared); return; }
        var ct = TestContext.Current.CancellationToken;
        await fx.Stores.EnsureResolvedAsync(WorkPriority.Interactive, ct);

        var now = DateTimeOffset.UtcNow;
        var originalId = await fx.RunAsync("finding a mail to reply to", app =>
        {
            var inbox = fx.Mail.ResolveFolder(app, shared, "Inbox");
            return fx.Mail.EnumerateRange(app, inbox, now.AddDays(-30), now.AddMinutes(1), 1).Rows.FirstOrDefault()?.EntryId;
        });
        if (originalId is null)
        {
            await fx.EnsureSyncedAsync();
            originalId = fx.Indexes.Search(new SearchFilter { Limit = 1 }, "").FirstOrDefault()?.Email.Id;
        }
        if (originalId is null) Assert.Skip(SkipTexts.NoData("mail to reply to"));
        var marker = $"[{fx.RunToken}] reply text";

        await fx.WithCleanupAsync(["", shared], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            var reply = await ReplyTool().RunAsync(originalId, marker, false, false, shared, ct);
            var sharedStore = await fx.RunAsync("reading the shared store id", app => fx.StoreIdOf(app, shared));
            scope.Track(new TrackedItem(TrackedKind.Draft, shared, reply.Id, sharedStore, Tagged(reply.Subject)));

            Assert.Equal(shared, reply.Mailbox);
            var f = await fx.RunAsync("reading the shared reply", app => ReadDraft(app, shared, reply.Id, tag: true));
            Assert.Equal(f.DraftsId, f.ParentId);
            Assert.Equal(sharedStore, f.ParentStoreId);
            Assert.Equal(shared, f.OnBehalf.Trim(), ignoreCase: true);
            Assert.StartsWith("Re:: ", f.Subject, StringComparison.Ordinal);
            Assert.False(f.Sent);
            Assert.Contains(marker, f.Html, StringComparison.Ordinal);

            // A Move that left a copy behind would show in the own Drafts folder.
            var own = await fx.RunAsync("searching own Drafts", app =>
                fx.TokenSubjectsIn(app, "", Outlook.OlDefaultFolders.olFolderDrafts));
            Assert.Empty(own);
            fx.Result("reply-shared-move-before-save", "ok");
        });
    }
}
