using System.Diagnostics;
using System.Globalization;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// M16 against the real classic Outlook (SPEC 23.11): the fingerprint of a saved AI draft, the guard refusals and the
/// one real guarded move into the own Deleted Items. Every draft is synthetic and self-addressed, saved and never sent;
/// the tests delete only the items they created, by EntryID, through the fixture's token-checked cleanup. No Send/Receive
/// is triggered. No window is opened. Nothing here prints mail text or addresses.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveObsoleteDraftTests(LiveOutlook fx)
{
    private static readonly Outlook.OlDefaultFolders[] LeftIn =
        [Outlook.OlDefaultFolders.olFolderDrafts, Outlook.OlDefaultFolders.olFolderDeletedItems];

    private sealed class Case(LiveOutlook.Scope scope, string storeId, string sourceId, RepliedDraft made,
        OutlookAiMailbox mailbox, CancellationToken ct)
    {
        public LiveOutlook.Scope Scope { get; } = scope;
        public string StoreId { get; } = storeId;
        public string SourceId { get; } = sourceId;
        public RepliedDraft Made { get; } = made;
        public OutlookAiMailbox Mailbox { get; } = mailbox;
        public CancellationToken Ct { get; } = ct;
        public bool Tagged { get; set; }
        public Stopwatch SinceCreated { get; } = Stopwatch.StartNew();

        public Task<AiDraftInfo?> ReadAsync() =>
            Mailbox.ReadAiDraftAsync("", Made.EntryId, WorkPriority.Interactive, Ct);
    }

    /// <summary>The common shape (brief A): source, reply draft, body, then tag whatever is still in Drafts.</summary>
    private async Task RunCaseAsync(string title, string marker, WorkPriority priority, CancellationToken ct, Func<Case, Task> body)
    {
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();
        await fx.WithCleanupAsync([""], LeftIn, async scope =>
        {
            var (sourceId, storeId) = await LiveObsoleteDrafts.CreateSourceAsync(fx, scope, me, title, ct);
            var made = await LiveObsoleteDrafts.CreateReplyAsync(fx, scope, sourceId, storeId, marker, priority, ct);
            var c = new Case(scope, storeId, sourceId, made, new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores), ct);
            try
            {
                await body(c);
            }
            finally
            {
                if (!c.Tagged)
                {
                    try
                    {
                        await LiveObsoleteDrafts.TagAsync(fx, made.EntryId);
                    }
                    catch (Exception)
                    {
                        fx.Result("cleanup-tag", "failed");
                    }
                }
            }
        });
    }

    private async Task<DraftFacts> ReadFactsAsync(string entryId) =>
        await fx.RunAsync("reading the draft", app => LiveDrafts.ReadDraft(fx, app, "", entryId));

    private async Task AssertStaysInDraftsAsync(string entryId, string subject)
    {
        var facts = await ReadFactsAsync(entryId);
        Assert.True(facts.ParentId == facts.DraftsId, "The draft is no longer in the own Drafts folder.");
        Assert.True(!facts.Sent, "The draft is marked as sent.");
        var found = await LiveObsoleteDrafts.FindInDeletedItemsAsync(fx, subject);
        Assert.True(found.Count == 0, "The draft is in Deleted Items.");
    }

    private void FailUnstable(string stage, FingerprintFields f0, FingerprintFields? fN)
    {
        IReadOnlyList<string> diff = fN is null ? [] : f0.DiffFrom(fN);
        var names = diff.Count == 0 ? "unknown" : string.Join(", ", diff);
        fx.Result("m16-fingerprint", $"UNSTABLE after {stage}; fields: {names}; mode {fx.ConnectionMode}");
        Assert.Fail($"The AI draft's fingerprint changed without an edit (after {stage}; fields: {names}). "
            + "Stop: SPEC 23.4's unedited test must be revisited before M16 ships. Nothing was moved or sent.");
    }

    [Fact(Timeout = 420_000)]
    public Task Ai_draft_fingerprint_is_stable_after_save_and_two_minutes() =>
        RunCaseAsync("M16 fingerprint", DraftMarker.Ai, WorkPriority.Background, TestContext.Current.CancellationToken, async c =>
        {
            var made = c.Made;
            Assert.True(DraftFingerprint.IsValid(made.Fingerprint), "The AI reply draft came back without a fingerprint.");
            Assert.StartsWith("Re:: ", made.Subject, StringComparison.Ordinal);
            var fp = made.Fingerprint!;

            // Right after Save().
            var f0 = await LiveObsoleteDrafts.FieldsAsync(fx, made.EntryId, c.StoreId);
            var i1 = await c.ReadAsync();
            Assert.True(i1 is not null, "The AI draft could not be read back.");
            if (i1.Fingerprint != fp)
                FailUnstable("save", f0, await LiveObsoleteDrafts.FieldsAsync(fx, made.EntryId, c.StoreId));
            Assert.True(AiDraftGuard.Evaluate(i1, fp) == ObsoleteDraftStatus.Movable, "The fresh AI draft is not Movable.");
            Assert.True(!i1.Open && i1.InDrafts && !i1.Sent && i1.Marker == DraftMarker.Ai,
                "The fresh AI draft is open, outside Drafts, sent or not marked ai.");

            // Cached-mode sync happens on its own; no Send/Receive is triggered.
            var remaining = TimeSpan.FromSeconds(LiveObsoleteDrafts.StabilitySeconds) - c.SinceCreated.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, c.Ct);

            var i2 = await c.ReadAsync();
            var f2 = await LiveObsoleteDrafts.FieldsAsync(fx, made.EntryId, c.StoreId);
            Assert.True(i2 is not null, "The AI draft could not be read back after the wait.");
            const string stage = "2 min";
            if (i2.Fingerprint != fp) FailUnstable(stage, f0, f2);
            Assert.True(AiDraftGuard.Evaluate(i2, fp) == ObsoleteDraftStatus.Movable, "The AI draft is not Movable after the wait.");
            fx.Result("m16-fingerprint",
                $"stable for {((int)c.SinceCreated.Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s, mode {fx.ConnectionMode}");

            // Informational, never fails: a plain Save() with no change, as an autosave would do.
            try
            {
                await LiveObsoleteDrafts.ResaveAsync(fx, made.EntryId, c.StoreId);
                var i3 = await c.ReadAsync();
                if (i3 is not null && i3.Fingerprint == fp)
                {
                    fx.Result("m16-resave", "stable");
                }
                else
                {
                    var f3 = await LiveObsoleteDrafts.FieldsAsync(fx, made.EntryId, c.StoreId);
                    var diff = f0.DiffFrom(f3);
                    fx.Result("m16-resave", "changed: " + (diff.Count == 0 ? "unknown" : string.Join(", ", diff)));
                }
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                fx.Result("m16-resave", "not checked");
            }
        });

    [Fact(Timeout = 180_000)]
    public Task Edited_ai_draft_is_changed_and_never_moved() =>
        RunCaseAsync("M16 edited", DraftMarker.Ai, WorkPriority.Interactive, TestContext.Current.CancellationToken, async c =>
        {
            var made = c.Made;
            Assert.True(DraftFingerprint.IsValid(made.Fingerprint), "The AI reply draft came back without a fingerprint.");
            var fp0 = made.Fingerprint!;

            await LiveObsoleteDrafts.EditBodyAsync(fx, made.EntryId, c.StoreId);
            var i = await c.ReadAsync();
            Assert.True(AiDraftGuard.Evaluate(i, fp0) == ObsoleteDraftStatus.Changed, "A COM edit of the body did not give Changed.");

            var m = await c.Mailbox.MoveObsoleteAiDraftAsync("", made.EntryId, fp0, WorkPriority.Interactive, c.Ct);
            Assert.True(m.Status == ObsoleteDraftStatus.Changed, "The guarded move did not report Changed for the edited draft.");
            Assert.True(m.Subject == made.Subject, "The guarded move reported another subject.");
            await AssertStaysInDraftsAsync(made.EntryId, made.Subject);
            fx.Result("m16-edited", "changed, stayed in Drafts");
        });

    [Fact(Timeout = 180_000)]
    public Task Unedited_ai_draft_moves_to_own_deleted_items_unsent() =>
        RunCaseAsync("M16 move", DraftMarker.Ai, WorkPriority.Interactive, TestContext.Current.CancellationToken, async c =>
        {
            var made = c.Made;
            var before = await fx.SnapshotSentAsync("");
            Assert.True(DraftFingerprint.IsValid(made.Fingerprint), "The AI reply draft came back without a fingerprint.");
            var fp0 = made.Fingerprint!;
            var fresh = await c.ReadAsync();
            Assert.True(AiDraftGuard.Evaluate(fresh, fp0) == ObsoleteDraftStatus.Movable, "The fresh AI draft is not Movable.");

            // The tag lets the token-checked cleanup remove the draft later; it is a subject edit, so it gives Changed
            // against the creation fingerprint.
            await LiveObsoleteDrafts.TagAsync(fx, made.EntryId);
            c.Tagged = true;
            var tagged = "[" + fx.RunToken + "] " + made.Subject;
            var i = await c.ReadAsync();
            Assert.True(AiDraftGuard.Evaluate(i, fp0) == ObsoleteDraftStatus.Changed, "A subject edit did not give Changed.");
            var fpT = i!.Fingerprint;
            Assert.True(AiDraftGuard.Evaluate(i, fpT) == ObsoleteDraftStatus.Movable, "The tagged AI draft is not Movable.");

            var m = await c.Mailbox.MoveObsoleteAiDraftAsync("", made.EntryId, fpT, WorkPriority.Interactive, c.Ct);
            Assert.True(m.Status == ObsoleteDraftStatus.Moved, "The guarded move did not move the unedited AI draft.");
            Assert.True(m.Subject == tagged, "The guarded move reported another subject.");

            var old = await c.ReadAsync();
            Assert.True(old is null || !old.InDrafts, "The moved draft is still in Drafts.");
            var inDrafts = await fx.RunAsync("listing Drafts", app =>
                fx.TokenSubjectsIn(app, "", Outlook.OlDefaultFolders.olFolderDrafts));
            Assert.True(!inDrafts.Contains(tagged), "The moved draft is still listed in Drafts.");

            var found = await LiveObsoleteDrafts.FindInDeletedItemsAsync(fx, tagged);
            Assert.True(found.Count == 1, "The moved draft is not exactly once in Deleted Items.");
            Assert.True(!found[0].Sent, "The moved draft is marked as sent.");
            Assert.True(found[0].Marker == DraftMarker.Ai, "The moved draft lost its ai marker.");
            Assert.True(string.Equals(found[0].MessageClass, "IPM.Note", StringComparison.OrdinalIgnoreCase),
                "The moved draft has another message class.");
            // The cleanup removes it from Deleted Items by this EntryID.
            c.Scope.Track(new TrackedItem(TrackedKind.Draft, "", found[0].EntryId, c.StoreId, tagged));

            var again = await c.Mailbox.MoveObsoleteAiDraftAsync("", made.EntryId, fpT, WorkPriority.Interactive, c.Ct);
            Assert.True(again.Status is ObsoleteDraftStatus.Gone or ObsoleteDraftStatus.NotInDrafts,
                "A second guarded move did not report Gone or NotInDrafts.");
            var stillOne = await LiveObsoleteDrafts.FindInDeletedItemsAsync(fx, tagged);
            Assert.True(stillOne.Count == 1, "The second guarded move changed Deleted Items.");

            await fx.AssertNothingSentAsync(before);
            fx.Result("m16-move", $"moved to own Deleted Items, unsent; second attempt {again.Status}");
        });

    [Fact(Timeout = 180_000)]
    public Task Draft_without_ai_marker_is_not_marked_and_stays() =>
        RunCaseAsync("M16 not marked", DraftMarker.Tool, WorkPriority.Interactive, TestContext.Current.CancellationToken, async c =>
        {
            var made = c.Made;
            Assert.True(DraftFingerprint.IsValid(made.Fingerprint), "The reply draft came back without a fingerprint.");
            var fp = made.Fingerprint!;

            var i = await c.ReadAsync();
            Assert.True(i is not null && i.Marker == DraftMarker.Tool, "The reply draft does not carry the tool marker.");
            Assert.True(AiDraftGuard.Evaluate(i, fp) == ObsoleteDraftStatus.NotMarked, "A draft without the ai marker is not NotMarked.");

            var m = await c.Mailbox.MoveObsoleteAiDraftAsync("", made.EntryId, fp, WorkPriority.Interactive, c.Ct);
            Assert.True(m.Status == ObsoleteDraftStatus.NotMarked, "The guarded move did not report NotMarked.");
            await AssertStaysInDraftsAsync(made.EntryId, made.Subject);
            fx.Result("m16-not-marked", "not marked, stayed in Drafts");
        });

    [Fact(Timeout = 180_000)]
    public Task Open_ai_draft_is_never_moved() =>
        RunCaseAsync("M16 open", DraftMarker.Ai, WorkPriority.Interactive, TestContext.Current.CancellationToken, async c =>
        {
            var made = c.Made;
            Assert.True(DraftFingerprint.IsValid(made.Fingerprint), "The AI reply draft came back without a fingerprint.");
            var fp0 = made.Fingerprint!;

            var inspector = await LiveObsoleteDrafts.HoldInspectorAsync(fx, made.EntryId, c.StoreId);
            try
            {
                var i = await c.ReadAsync();
                Assert.True(i is not null, "The AI draft could not be read back.");
                if (!i.Open)
                {
                    await inspector.DisposeAsync();
                    fx.Result("m16-open", "inconclusive: a hidden inspector is not listed in Inspectors");
                    Assert.Skip("Outlook didn't list the hidden inspector, so the open-draft check can't run here without opening a window. Nothing was moved or sent.");
                }
                Assert.True(AiDraftGuard.Evaluate(i, fp0) == ObsoleteDraftStatus.Open, "An open AI draft is not Open.");

                var m = await c.Mailbox.MoveObsoleteAiDraftAsync("", made.EntryId, fp0, WorkPriority.Interactive, c.Ct);
                Assert.True(m.Status == ObsoleteDraftStatus.Open, "The guarded move did not report Open.");
                await AssertStaysInDraftsAsync(made.EntryId, made.Subject);
            }
            finally
            {
                await inspector.DisposeAsync();
            }

            // Informational: after the close the draft should be Movable again (Changed is recorded, not failed).
            var after = await c.ReadAsync();
            fx.Result("m16-open", $"open, stayed in Drafts; after close: {AiDraftGuard.Evaluate(after, fp0)}");
        });
}
