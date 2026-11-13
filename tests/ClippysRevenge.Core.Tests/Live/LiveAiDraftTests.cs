using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// The manual AI draft path against real Outlook and the real Claude CLI (SPEC 19.7, 19.12). The source is a synthetic,
/// self-addressed, unsent item that the test saves in the own Drafts folder; no real mail goes to Claude. Needs
/// CLIPPY_LIVE=1 and CLIPPY_LIVE_CLAUDE=1. Uses claude-haiku-5-5 with effort low (one call), not the app default
/// claude-opus-5-5. Reply drafts are saved, never sent. Nothing here prints mail text, prompts or model output.
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("live", "claude")]
[Trait("os", "windows")]
public sealed class LiveAiDraftTests(LiveOutlook fx)
{
    /// <summary>Outlook's own reply to the unsent source, in memory only (discarded). Worker thread only.</summary>
    private static (string? Html, int HResult) OutlooksOwnReply(Outlook.Application app, string id, string storeId)
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
            LiveDrafts.Rel(reply);
            LiveDrafts.Rel(item);
            LiveDrafts.Rel(session);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task Manual_draft_round_trip_saves_ai_reply_and_sends_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Gate, Outlook, own address.
        var model = LiveClaude.RequireEnabled();
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();

        // 2. Settings with consent, Haiku, effort low. The fixture's Settings is never changed.
        var ai = LiveClaude.Settings(model, DateTimeOffset.UtcNow);
        var s = fx.Settings.Clone();
        s.AiDrafting = ai;
        var cli = LiveClaude.CreateModel(() => s.AiDrafting, Path.Combine(fx.TempRoot, "ai"));
        await LiveClaude.RequireReadyAsync(cli, ct);

        // 3. Own store.
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));

        // 4. Create, draft, check, clean up.
        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            // 4.1 Synthetic source: self-addressed, unsent, in own Drafts, no signature.
            var subject = $"[{fx.RunToken}] AI round trip";
            var body = string.Join("\n",
                "Hi,",
                "",
                "Could you tell me which day next week works for a 30-minute call about the test plan? Please include the link https://example.org/plan in your answer.",
                "",
                "Thanks,",
                "Sam",
                "",
                fx.RunToken);
            var src = await new CreateDraftTool(fx.Settings,
                    new Signatures(_ => null, Path.Combine(fx.TempRoot, "no-signatures")),
                    new OutlookDraftWriter(fx.Worker, fx.Stores), fx.Hub)
                .RunAsync(new CreateDraftArgs(Recipients.Format([me]), subject, body), ct);
            scope.Track(new TrackedItem(TrackedKind.Draft, "", src.Id, ownStore, subject));

            // 4.2 Baseline: Outlook's own reply to it (nothing is saved). Before any Claude call.
            var (baselineHtml, baselineHResult) = await fx.RunAsync("checking Outlook's own reply",
                app => OutlooksOwnReply(app, src.Id, ownStore));
            if (baselineHtml is null)
            {
                fx.Result("ai-reply-baseline",
                    $"skipped: reply to an unsent item failed (HResult 0x{baselineHResult.ToString("x8", CultureInfo.InvariantCulture)})");
                Assert.Skip("Outlook can't reply to an unsent test item on this profile, so the AI round trip can't run here. Nothing was sent.");
            }
            fx.Result("ai-reply-baseline", "ok");
            var baseline = AiHtmlChecks.Active(baselineHtml);

            // 4.3 Wiring: every service is real except the selection and the guards.
            RepliedDraft? made = null;
            var aiMailbox = new FixedSelectionMailbox(
                new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores), new SelectedEmail(src.Id, ownStore));
            var guard = new GuardedModel(cli, model, fx.RunToken);
            var writer = new RecordingReplyWriter(new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores), src.Id,
                m =>
                {
                    made = m;
                    scope.Track(new TrackedItem(TrackedKind.Draft, "", m.EntryId, ownStore, $"[{fx.RunToken}] {m.Subject}"));
                });
            using var hub = new ActivityHub();
            using var drafts = new DraftWorker(new DraftWorkerServices(() => s, fx.Indexes, aiMailbox, writer, guard,
                NoStyleGuide.Instance, () => null, hub, new BalloonCenter(),
                () => fx.Worker.State == OutlookConnectionState.Available, () => false, TimeZoneInfo.Local));

            // 4.4 Run the production manual path.
            var watch = Stopwatch.StartNew();
            var result = await drafts.DraftSelectedAsync(ct);
            watch.Stop();

            // 4.5 Tag the reply at once, so cleanup can find it.
            DraftFacts? f = null;
            if (writer.Plans.Count == 1 && made is not null)
            {
                var entryId = made.EntryId;
                f = await fx.RunAsync("reading the AI reply", app => LiveDrafts.ReadDraft(fx, app, "", entryId, tag: true));
            }

            // 4.6 Outcome.
            if (guard.Tripped is not null)
                Assert.Fail("The live guard refused the Claude request (" + guard.Tripped + "). Nothing was sent to Claude.");
            if (guard.LastResult is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
            const string notReadyPrefix = "Claude isn't ready: ";
            if (result.Message.StartsWith(notReadyPrefix, StringComparison.Ordinal))
                Assert.Skip(LiveClaude.NotReady(result.Message[notReadyPrefix.Length..]));
            Assert.True(result.Drafted, "Manual draft failed: " + result.Message);

            // 4.7 Assertions.
            Assert.True(result.Message == DraftTexts.ManualDone, "The manual draft gave an unexpected message.");
            Assert.True(aiMailbox.SelectionCalls == 1, "The fixed selection was not used exactly once.");
            Assert.True(guard.Calls == 1, "Claude was not called exactly once.");
            Assert.True(guard.LastResult is ModelResult.Ok, "The Claude call did not give a result.");

            Assert.True(writer.Plans.Count == 1, "Exactly one reply plan was expected.");
            var plan = writer.Plans[0];
            Assert.True(plan.Id == src.Id, "The reply plan is for another item.");
            Assert.True(plan.Mailbox is null, "The reply plan names a mailbox.");
            Assert.True(plan.Marker == DraftMarker.Ai, "The reply plan is not marked ai.");
            Assert.True(plan.Priority == WorkPriority.Interactive, "The manual reply plan is not interactive.");
            Assert.True(!plan.SignatureAdded, "The reply plan added a signature.");
            Assert.True(AiHtmlChecks.FragmentIsSanitized(plan.HtmlFragment), "The AI fragment has a tag outside the allow-list.");

            Assert.True(f is not null && made is not null, "The AI reply was not read back.");
            var expectedSubject = ReplySubject.Normalize(subject);
            Assert.True(f.Subject == expectedSubject, "The reply subject is not the normalized source subject.");
            Assert.StartsWith("Re:: ", f.Subject, StringComparison.Ordinal);
            Assert.True(!f.Sent, "The reply draft is marked as sent.");
            Assert.True(f.ParentId == f.DraftsId, "The reply draft is not in the own Drafts folder.");
            Assert.True(f.ClippyDraft == DraftMarker.Ai, "The reply draft does not carry the ai marker.");

            var saved = AiHtmlChecks.Active(f.Html);
            Assert.True(saved.Scripts == 0 && saved.Links <= baseline.Links && saved.Images <= baseline.Images,
                "The saved reply has more links/images than Outlook's own reply.");
            Assert.True(AiHtmlChecks.StartOfFragmentIn(plan.HtmlFragment, f.Text), "The AI text is not in the saved reply.");

            var srcAfter = await fx.RunAsync("reading the source again", app => LiveDrafts.ReadDraft(fx, app, "", src.Id));
            Assert.True(!srcAfter.Sent && srcAfter.Subject == subject, "The source item changed.");

            var decision = fx.Indexes.For("").GetDecision(src.Id);
            Assert.True(decision is not null, "No decision row was written.");
            Assert.True(decision.Outcome == AiOutcome.Drafted, "The decision row is not Drafted.");
            Assert.True(decision.ModelCalls == 1 && decision.Attempts == 0, "The decision row has unexpected counts.");
            Assert.True(decision.DraftEntryId == made.EntryId, "The decision row points to another draft.");
            Assert.True(decision.ConversationTopic is null, "The decision row stores a conversation topic.");
            Assert.True(drafts.ModelCallsToday() >= 1, "The model call was not counted.");

            var draftEvents = hub.GetEvents().Where(e => e.Kind == ActivityKind.Draft).ToList();
            Assert.True(draftEvents.Count == 1 && draftEvents[0].Summary == DraftTexts.DraftCreated(made.Subject),
                "The activity log does not show exactly one draft event.");

            // 4.8 Result line.
            var placeholders = plan.HtmlFragment.Contains(DraftHtml.HighlightOpen, StringComparison.Ordinal) ? "yes" : "no";
            var confidence = decision.Confidence is { } c ? c.ToString("0.00", CultureInfo.InvariantCulture) : "?";
            fx.Result("ai-roundtrip",
                $"drafted in {AiHtmlChecks.Seconds(watch.Elapsed)} s, model {model}, confidence {confidence}, "
                + $"placeholders={placeholders}, links={AiHtmlChecks.Active(plan.HtmlFragment).Links.ToString(CultureInfo.InvariantCulture)}");
        });
    }
}
