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
        ai.MeetingTimes = false;   // the email asks for a call; keep this test on the plain reply path (no step 6a)
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
            var guard = new GuardedModel(cli, model, fx.RunToken, meetingNote: false);
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

    [Fact(Timeout = 300_000)]
    public async Task Manual_draft_offers_meeting_times_and_sends_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Gate, Outlook, own address.
        var model = LiveClaude.RequireEnabled();
        await fx.RequireReadyAsync(ct);
        var me = await fx.MeAsync();

        // 2. Settings: consent, Haiku, effort low, meeting times on with three options.
        var ai = LiveClaude.Settings(model, DateTimeOffset.UtcNow);
        ai.MeetingTimes = true;
        ai.MeetingTimeOptions = 3;
        var s = fx.Settings.Clone();
        s.AiDrafting = ai;

        // 3. Pre-check, before any Claude call: is there a free 30-minute time in the next three weeks?
        FreeTimesResult pre;
        try
        {
            pre = await new FreeTimeFinder(new OutlookFreeBusySource(fx.Worker), TimeProvider.System, TimeZoneInfo.Local)
                .FindAsync(new FreeTimeRequest(null, DurationMinutes: 30, DaysAhead: 21,
                    DateFrom: DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), MaxSuggestions: 1), s, ct);
        }
        catch (OutlookNotRespondingException)
        {
            Assert.Skip(SkipTexts.NotResponding("find_free_times", 30));
            return;
        }
        if (pre.UnknownAttendees.Count > 0)
        {
            fx.Result("ai-meeting-times", "skipped: own free/busy unknown");
            Assert.Skip("Outlook returned no free/busy for your own calendar, so meeting times can't be offered. Nothing was sent to Claude.");
        }
        if (pre.Offers.Count == 0)
        {
            fx.Result("ai-meeting-times", "skipped: no free time in 3 weeks");
            Assert.Skip("No free 30-minute time in the next three weeks, so meeting times can't be offered. Nothing was sent to Claude.");
        }

        // 4. Model.
        var cli = LiveClaude.CreateModel(() => s.AiDrafting, Path.Combine(fx.TempRoot, "ai-meeting"));
        await LiveClaude.RequireReadyAsync(cli, ct);

        // 5. Own store.
        var ownStore = await fx.RunAsync("reading the store id", app => fx.StoreIdOf(app, ""));

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderDrafts, async scope =>
        {
            // 5.1 Synthetic source: self-addressed, unsent, in own Drafts, no signature.
            var subject = $"[{fx.RunToken}] AI meeting times";
            var body = string.Join("\n",
                "Hi,",
                "",
                "Can we meet next week for 30 minutes to go over the test plan? Please suggest a few times that work for you.",
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

            // 5.2 Baseline: Outlook can reply to an unsent item (nothing is saved). Before any Claude call.
            var (baselineHtml, baselineHResult) = await fx.RunAsync("checking Outlook's own reply",
                app => OutlooksOwnReply(app, src.Id, ownStore));
            if (baselineHtml is null)
            {
                fx.Result("ai-meeting-baseline",
                    $"skipped: reply to an unsent item failed (HResult 0x{baselineHResult.ToString("x8", CultureInfo.InvariantCulture)})");
                Assert.Skip("Outlook can't reply to an unsent test item on this profile, so the AI meeting-times test can't run here. Nothing was sent.");
            }
            fx.Result("ai-meeting-baseline", "ok");

            // 5.3 Wiring: every service is real except the selection and the guards.
            RepliedDraft? made = null;
            var aiMailbox = new FixedSelectionMailbox(
                new OutlookAiMailbox(fx.Worker, fx.Mail, fx.Stores), new SelectedEmail(src.Id, ownStore));
            var guard = new GuardedModel(cli, model, fx.RunToken, meetingNote: true);
            var fb = new GuardedFreeBusySource(new OutlookFreeBusySource(fx.Worker), me.Email, () => guard.LastResult is not null);
            var writer = new RecordingReplyWriter(new OutlookReplyDraftWriter(fx.Worker, fx.Mail, fx.Stores), src.Id,
                m =>
                {
                    made = m;
                    scope.Track(new TrackedItem(TrackedKind.Draft, "", m.EntryId, ownStore, $"[{fx.RunToken}] {m.Subject}"));
                });
            using var hub = new ActivityHub();
            using var drafts = new DraftWorker(new DraftWorkerServices(() => s, fx.Indexes, aiMailbox, writer, guard,
                NoStyleGuide.Instance, () => null, hub, new BalloonCenter(),
                () => fx.Worker.State == OutlookConnectionState.Available, () => false, TimeZoneInfo.Local,
                FreeBusy: fb));

            // 5.4 Run the production manual path; tag the reply at once, so cleanup can find it.
            var watch = Stopwatch.StartNew();
            var result = await drafts.DraftSelectedAsync(ct);
            watch.Stop();
            DraftFacts? f = null;
            if (writer.Plans.Count == 1 && made is not null)
            {
                var entryId = made.EntryId;
                f = await fx.RunAsync("reading the AI reply", app => LiveDrafts.ReadDraft(fx, app, "", entryId, tag: true));
            }

            // 5.5 Outcome.
            if (guard.Tripped is not null)
                Assert.Fail("The live guard refused the Claude request (" + guard.Tripped + "). Nothing was sent to Claude.");
            if (fb.Tripped is not null)
                Assert.Fail("The live guard refused a free/busy request (" + fb.Tripped + ").");
            if (guard.LastResult is ModelResult.RateLimited) Assert.Skip(LiveClaude.UsageLimit);
            const string notReadyPrefix = "Claude isn't ready: ";
            if (result.Message.StartsWith(notReadyPrefix, StringComparison.Ordinal))
                Assert.Skip(LiveClaude.NotReady(result.Message[notReadyPrefix.Length..]));
            Assert.True(result.Drafted, "Manual draft failed: " + result.Message);

            // 5.6 Classify the inserted block.
            Assert.True(writer.Plans.Count == 1, "Exactly one reply plan was expected.");
            var plan = writer.Plans[0];
            var lines = LiveMeetingChecks.HtmlLines(plan.HtmlFragment);
            var offers = lines.Select(LiveMeetingChecks.Parse).OfType<OfferLine>().ToList();
            var plain = DraftHtml.ToPlainText(plan.HtmlFragment);
            Assert.True(!MeetingTimesHtml.HasPlaceholder(plan.HtmlFragment), "A [[meeting_times]] placeholder is left in the reply.");
            if (plain.Contains(MeetingTimesHtml.CalendarUnreadable, StringComparison.Ordinal))
                Assert.Fail("Clippy couldn't read your calendar for the offers, although the pre-check could.");
            if (plain.Contains("[[add meeting times: no free time found between ", StringComparison.Ordinal))
            {
                fx.Result("ai-meeting-times", "inconclusive: no free time in the range Claude asked for");
                Assert.Skip("No free time in the range Claude asked for, so the offer lines couldn't be checked. Nothing was sent.");
            }
            if (offers.Count == 0)
                Assert.Fail(fb.Calls == 0
                    ? "Claude did not mark the email as a meeting request (no meeting object and no [[meeting_times]] placeholder)."
                    : "Free/busy was read, but the reply has no SPEC 21.5 option lines.");

            // 5.7 Assertions.
            Assert.True(offers.Count is >= 1 and <= 3, "The number of offered times is outside 1..3.");
            Assert.True(offers.Select((o, i) => o.N == i + 1).All(x => x), "The offered times are not numbered 1, 2, 3 in order.");
            var minutes = LiveMeetingChecks.Minutes(offers[0].Start, offers[0].End);
            Assert.True(offers.All(o => LiveMeetingChecks.Minutes(o.Start, o.End) == minutes), "The offered times have different lengths.");
            Assert.True(minutes is 30 or 60, "The offered times are neither 30 nor 60 minutes long.");
            var cetShown = offers[0].CetStart is not null;
            Assert.True(offers.All(o => (o.CetStart is not null) == cetShown), "Only some offered times have the CET part.");

            var first = lines.ToList().FindIndex(l => LiveMeetingChecks.Parse(l) is not null);
            var last = lines.ToList().FindLastIndex(l => LiveMeetingChecks.Parse(l) is not null);
            for (var i = first; i <= last; i++)
            {
                if (LiveMeetingChecks.Parse(lines[i]) is not null) continue;
                Assert.True(lines[i] == LiveMeetingChecks.DefaultHeading || lines[i].EndsWith(':'),
                    "A line between the offered times is neither a heading nor an option.");
            }

            Assert.True(fb.Calls >= 1, "Free/busy was not read.");
            Assert.True(fb.Priorities.All(p => p == WorkPriority.Interactive), "A free/busy read was not interactive.");
            Assert.True(guard.Calls == 1, "Claude was not called exactly once.");
            Assert.True(guard.LastResult is ModelResult.Ok, "The Claude call did not give a result.");
            Assert.True(aiMailbox.SelectionCalls == 1, "The fixed selection was not used exactly once.");

            Assert.True(plan.Id == src.Id, "The reply plan is for another item.");
            Assert.True(plan.Mailbox is null, "The reply plan names a mailbox.");
            Assert.True(plan.Marker == DraftMarker.Ai, "The reply plan is not marked ai.");
            Assert.True(plan.Priority == WorkPriority.Interactive, "The manual reply plan is not interactive.");
            Assert.True(!plan.SignatureAdded, "The reply plan added a signature.");
            Assert.True(AiHtmlChecks.FragmentIsSanitized(plan.HtmlFragment), "The AI fragment has a tag outside the allow-list.");

            Assert.True(f is not null && made is not null, "The AI reply was not read back.");
            Assert.True(f.Subject == ReplySubject.Normalize(subject), "The reply subject is not the normalized source subject.");
            Assert.StartsWith("Re:: ", f.Subject, StringComparison.Ordinal);
            Assert.True(!f.Sent, "The reply draft is marked as sent.");
            Assert.True(f.ParentId == f.DraftsId, "The reply draft is not in the own Drafts folder.");
            Assert.True(f.ClippyDraft == DraftMarker.Ai, "The reply draft does not carry the ai marker.");
            Assert.True(!MeetingTimesHtml.HasPlaceholder(f.Text), "A [[meeting_times]] placeholder is left in the saved reply.");
            var savedText = LiveMeetingChecks.Collapse(f.Text);
            foreach (var l in lines)
                if (LiveMeetingChecks.Parse(l) is not null)
                    Assert.True(savedText.Contains(LiveMeetingChecks.Collapse(l), StringComparison.Ordinal),
                        "The saved reply does not show the offered times.");

            var srcAfter = await fx.RunAsync("reading the source again", app => LiveDrafts.ReadDraft(fx, app, "", src.Id));
            Assert.True(!srcAfter.Sent && srcAfter.Subject == subject, "The source item changed.");

            var decision = fx.Indexes.For("").GetDecision(src.Id);
            Assert.True(decision is not null, "No decision row was written.");
            Assert.True(decision.Outcome == AiOutcome.Drafted, "The decision row is not Drafted.");
            Assert.True(decision.ModelCalls == 1 && decision.Attempts == 0, "The decision row has unexpected counts.");
            Assert.True(decision.DraftEntryId == made.EntryId, "The decision row points to another draft.");

            var draftEvents = hub.GetEvents().Where(e => e.Kind == ActivityKind.Draft).ToList();
            Assert.True(draftEvents.Count == 1 && draftEvents[0].Summary == DraftTexts.DraftCreated(made.Subject),
                "The activity log does not show exactly one draft event.");

            // 5.8 Result line.
            fx.Result("ai-meeting-times",
                $"offered {offers.Count.ToString(CultureInfo.InvariantCulture)} times ({minutes.ToString(CultureInfo.InvariantCulture)} min, "
                + $"cet={(cetShown ? "yes" : "no")}) in {AiHtmlChecks.Seconds(watch.Elapsed)} s, model {model}, "
                + $"free/busy calls {fb.Calls.ToString(CultureInfo.InvariantCulture)}");
        });
    }
}
