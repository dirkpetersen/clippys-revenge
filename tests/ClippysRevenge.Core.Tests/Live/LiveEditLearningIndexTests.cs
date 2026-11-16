using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// M20 against the real classic Outlook (SPEC 27.8): a saved AI reply draft's ClippyDraft marker and PR_IN_REPLY_TO_ID
/// are readable from an index after a sync. The draft is made through the production reply writer, the sync runs into a
/// private temp index (never the user's index, never the fixture's shared one). Nothing is sent: the sent check
/// (Sent Items copy keeps ai and in-reply-to) is manual (M20.6). Only the draft this test created is deleted, through
/// the fixture's token-checked cleanup. Nothing here prints mail text or addresses.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveEditLearningIndexTests(LiveOutlook fx)
{
    [Fact(Timeout = 300_000)]
    public async Task Saved_ai_draft_marker_and_in_reply_to_are_in_the_index_after_a_sync()
    {
        var ct = TestContext.Current.CancellationToken;
        await fx.RequireReadyAsync(ct);
        await fx.EnsureSyncedAsync();

        // A real mail with an internet message id to answer (the reply's PR_IN_REPLY_TO_ID comes from it).
        string? sourceId = null, sourceImid = null;
        var shared = fx.Indexes.For("");
        foreach (var hit in fx.Indexes.Search(new SearchFilter { Limit = 25 }, ""))
        {
            var d = shared.Get(hit.Email.Id);
            if (d is not null && !string.IsNullOrWhiteSpace(d.InternetMessageId))
            {
                sourceId = d.EntryId;
                sourceImid = d.InternetMessageId.Trim();
                break;
            }
        }
        if (sourceId is null || sourceImid is null) Assert.Skip(SkipTexts.NoData("synced Inbox mail with an internet message id in the last month"));

        var tempIndex = new EmailIndex(Path.Combine(fx.TempRoot, "m20-edit-learning-index.db"));
        try
        {
            await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
            {
                var storeId = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));
                var plan = new ReplyPlan(sourceId, null, false, $"<p>[{fx.RunToken}] M20 live test</p>", false,
                    DraftMarker.Ai, WorkPriority.Background);
                var made = await new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores).ReplyAsync(plan, ct);
                var tagged = $"[{fx.RunToken}] {made.Subject}";
                scope.Track(new TrackedItem(TrackedKind.Draft, "", made.EntryId, storeId, tagged));
                await LiveObsoleteDrafts.TagAsync(fx, made.EntryId);

                var facts = await fx.RunAsync("reading the draft", app => LiveDrafts.ReadDraft(fx, app, "", made.EntryId));
                Assert.Equal(DraftMarker.Ai, facts.ClippyDraft);
                Assert.False(facts.Sent);
                Assert.Equal(facts.DraftsId, facts.ParentId);

                // A real sync cycle of the own Drafts folder into the private index.
                var source = new OutlookMailSource(fx.Worker, fx.Stores, fx.Mail);
                var engine = new SyncEngine(source, fx.Hub, logger: null, reconcile: null);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(3));
                await engine.SyncMailboxAsync(new MailboxPlan("", ["Drafts"], 1), tempIndex, cts.Token);

                var row = tempIndex.Get(made.EntryId);
                var viaSync = row is not null;
                if (row is null)
                {
                    // A draft has no received time, so the folder's time-range listing can miss it. The sync's own
                    // read step (the same ReadBatch/ReadForIndex path) then fills the row from the entry id.
                    var folder = await source.GetFolderAsync(
                        new SourceMailbox("", "", storeId, ""), "Drafts", ct);
                    Assert.True(folder is not null, "The own Drafts folder could not be resolved.");
                    var read = await source.ReadAsync(folder, [made.EntryId], ct);
                    Assert.True(read.Count == 1, "The draft could not be read for the index.");
                    tempIndex.UpsertBatch(read);
                    row = tempIndex.Get(made.EntryId);
                }

                Assert.True(row is not null, "The saved AI draft is not in the index.");
                Assert.Equal(DraftMarker.Ai, row.ClippyMarker);
                Assert.True(!string.IsNullOrWhiteSpace(row.InReplyTo), "The indexed AI draft has no in-reply-to id.");
                Assert.Equal(sourceImid, row.InReplyTo!.Trim());
                Assert.StartsWith("Drafts", row.Folder, StringComparison.OrdinalIgnoreCase);
                fx.Result("m20-index", $"ai marker and in-reply-to in the index, found by the folder sync={viaSync}");
            });
        }
        finally
        {
            tempIndex.Close();
        }
    }
}
