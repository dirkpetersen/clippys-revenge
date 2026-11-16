using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

// The revisit step of the automatic pass (SPEC 23.2, 23.6): re-evaluates Clippy's own tracked AI drafts when their
// thread moved on. Output only ever becomes a saved reply draft through IReplyDraftWriter; the old draft is moved
// (never deleted) only by IAiMailbox.MoveObsoleteAiDraftAsync, and only after the new one is saved.
// Logs and events never hold subjects, ids, addresses, draft text, prompts, model output or the model's reason.
public sealed partial class DraftWorker
{
    /// <summary>The fixed inputs of one mailbox's revisits.</summary>
    private sealed record RevisitScope(EmailIndex Index, string Key, string MailboxSmtp, Settings Settings,
        AiIdentity Identity, string UserSmtp, WorkPriority Priority);

    private void RevisitOutcome(string outcome) => _log?.LogDebug("AI drafts: revisit {Outcome}", outcome);

    /// <summary>SPEC 23.6 for one mailbox in an automatic pass. Null = go on with the candidates.</summary>
    private async Task<DraftPassEnd?> RevisitMailboxAsync(PassState p, RevisitScope s, CancellationToken ct)
    {
        IReadOnlyList<DueRevisit> due;
        try
        {
            due = s.Index.DueRevisits(_time.GetUtcNow() - TimeSpan.FromDays(DraftRevisit.MaxDraftAgeDays),
                DraftRevisit.MaxRevisitsPerMailbox);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: listing drafts to revisit failed ({Type}).", ex.GetType().Name);
            return null;
        }

        foreach (var d in due)
        {
            if (ct.IsCancellationRequested) return DraftPassEnd.Cancelled;
            if (Volatile.Read(ref _yield) > 0) return DraftPassEnd.Yielded;
            var cur = _s.Settings().Clone();
            if (!cur.AiDrafting.Enabled || !cur.AiDrafting.HasConsent) return DraftPassEnd.Disabled;
            if (!cur.AiDrafting.ReviseDrafts) return null;
            if (s.Key.Length > 0 && !SharedAiEnabled(cur, s.Key)) return null;

            try
            {
                var stop = await RevisitAsync(p, s with { Settings = cur }, d.Thread, d.UpTo, ct).ConfigureAwait(false);
                if (stop is { } e) return e;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An index failure: the draft is tried again in a later pass.
                _log?.LogWarning("AI drafts: a revisit failed ({Type}).", ex.GetType().Name);
            }
        }
        return null;
    }

    /// <summary>One revisit (SPEC 23.6 steps 1-7) of a tracked draft up to upTo; tallies into p.
    /// Null = go on with the next draft; a value ends the pass. Reused by the rescan (M16.6).</summary>
    private async Task<DraftPassEnd?> RevisitAsync(PassState p, RevisitScope s, AiDraftThread t, DateTimeOffset upTo,
        CancellationToken ct)
    {
        var id = t.Draft.DraftEntryId;
        var fp = t.Draft.Fingerprint;
        var oldChecked = t.Draft.Checked;
        var topic = t.ConversationTopic;
        var ai = s.Settings.AiDrafting;
        var now = _time.GetUtcNow();

        // 1. Did the user reply already?
        var newer = s.Index.ThreadInbox(topic, oldChecked, upTo, EmailIndex.MaxThreadInboxRows);
        var replied = s.Index.RepliedTo(null, topic, t.Draft.Created)
            || (s.Key.Length > 0 && _s.Indexes.For(MailboxIndexes.OwnMailbox).RepliedTo(null, topic, t.Draft.Created))
            || newer.Any(r => DraftRevisit.IsOwnAddress(r.SenderEmail, s.UserSmtp, s.MailboxSmtp));
        if (replied)
        {
            s.Index.SetAiDraftState(id, AiDraftState.Replied, null, t.Draft.Attempts, now);
            RevisitOutcome("replied");
            return null;
        }

        // 2. Read and guard.
        AiDraftInfo? info;
        try
        {
            info = await _s.Mailbox.ReadAiDraftAsync(s.Key, id, s.Priority, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            return DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: reading an AI draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            return null;
        }
        switch (AiDraftGuard.Evaluate(info, fp))
        {
            case ObsoleteDraftStatus.Gone or ObsoleteDraftStatus.NotInDrafts or ObsoleteDraftStatus.Sent:
                s.Index.SetAiDraftState(id, AiDraftState.Gone, null, t.Draft.Attempts, now);
                RevisitOutcome("gone");
                return null;
            case ObsoleteDraftStatus.NotMarked or ObsoleteDraftStatus.Changed:
                s.Index.SetAiDraftState(id, AiDraftState.Edited, null, t.Draft.Attempts, now);
                RevisitOutcome("edited");
                return null;
            case ObsoleteDraftStatus.Open:
                RevisitOutcome("open");
                return null;
            case ObsoleteDraftStatus.Movable:
                break;
            default:
                return null;
        }

        // 3. Reply target.
        var rows = s.Index.ThreadInbox(topic, t.SourceTime, upTo, EmailIndex.MaxThreadInboxRows);
        var target = DraftRevisit.PickReplyTarget(rows, s.MailboxSmtp, s.UserSmtp, ai);
        var targetId = target?.EntryId ?? t.Draft.SourceEntryId;
        var detail = s.Index.Get(targetId);
        if (detail is null && target is not null)
        {
            targetId = t.Draft.SourceEntryId;
            detail = s.Index.Get(targetId);
        }
        if (detail is null) return null;

        // 4. Limit and backoff.
        if (CapReached(ai.DailyCap))
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _s.Zone).DateTime);
            if (_limitReportedDay != today)
            {
                _limitReportedDay = today;
                _s.Hub.Event(ActivityKind.Ai, DraftTexts.DailyLimit(ai.DailyCap));
            }
            return DraftPassEnd.DailyLimit;
        }
        if (!p.Rescan && _retryAt is { } retry && retry > now) return DraftPassEnd.Backoff;

        // 5. Model call.
        var thread = DraftRevisit.PromptThread(s.Index.Thread(topic, upTo, DraftRevisit.ThreadFetch, targetId), id);
        var email = new PromptEmail(detail.SenderName, detail.SenderEmail, detail.To, detail.Cc, detail.EventTime,
            detail.Subject, NonInlineNames(detail.Attachments), detail.Body);
        var input = PromptInput(s.Settings, s.Key, email, thread);
        var related = FindRelated(ai, s.Index,
            new RelatedAnchor(targetId, detail.InternetMessageId, topic, detail.EventTime, detail.SenderEmail, detail.Subject, detail.Body),
            input.SignatureText, s.UserSmtp, s.MailboxSmtp);
        input = input with { Related = related };
        var request = new ModelRequest(
            DraftPrompt.RevisitSystemPrompt(s.Identity.DisplayName, s.UserSmtp, _s.Style.ReadForPrompt(), ai.MeetingTimes,
                _s.Facts?.ReadForPrompt() ?? "", DraftPrompt.HasRelated(input), ai.ReasonLine),
            DraftPrompt.RevisitUserPrompt(new RevisitPromptInput(input, info!.PlainBody, t.Draft.Created)),
            DraftModel.RevisitSchema, ai.Model, ai.Effort, TimeSpan.FromSeconds(ai.TimeoutSeconds),
            DraftModel.DraftMaxOutputTokens);

        var callCt = p.CallToken ?? ct;
        if (callCt.IsCancellationRequested) return DraftPassEnd.Cancelled;
        if (!p.Rescan)
        {
            p.Scope ??= _s.Hub.Begin(ActivityState.Drafting, ActivityHub.DraftingLabel);
            p.Total++;
            _s.Hub.Report(ActivityState.Drafting, p.Done, p.Total);
        }
        var result = await _s.Model.RunAsync(request with { Kind = ModelCallKind.Revisit }, callCt).ConfigureAwait(false);
        p.Usage = p.Usage.AddCall(result);
        if (!p.Rescan)
        {
            p.Done++;
            _s.Hub.Report(ActivityState.Drafting, p.Done, p.Total);
        }

        now = _time.GetUtcNow();
        RevisitReply? reply = null;
        ModelFailure? failure = null;
        switch (result)
        {
            case ModelResult.Ok ok:
                p.Calls++;
                p.Revisits++;
                reply = DraftRevisit.Parse(ok.Output);
                if (reply is null) failure = ModelFailure.BadOutput;
                break;

            case ModelResult.RateLimited rl:
                _retryAt = rl.RetryAt is { } at && at > now ? at + RateLimitMargin : now + RateLimitDefaultWait;
                _s.Hub.Event(ActivityKind.Ai, DraftTexts.RateLimited(
                    TimeZoneInfo.ConvertTime(_retryAt.Value, _s.Zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
                return DraftPassEnd.RateLimited;

            case ModelResult.NotSignedIn:
                EnterHold();
                return DraftPassEnd.NotSignedIn;

            case ModelResult.Failed { Kind: ModelFailure.Cancelled } when callCt.IsCancellationRequested:
                return DraftPassEnd.Cancelled;

            case ModelResult.Failed f:
                p.Calls++;
                p.Revisits++;
                failure = f.Kind;
                break;

            default:
                p.Calls++;
                p.Revisits++;
                failure = ModelFailure.BadOutput;
                break;
        }
        if (failure is { } kind) return CallFailed(p, s, t, upTo, kind, now);

        // 6. Result.
        CallSucceeded(p);
        if (reply!.Action != RevisitAction.Keep && reply.Confidence < ai.MinConfidence)
        {
            s.Index.AddRevisit(id, now, AiRevisitOutcome.LowConfidence, 1);
            s.Index.SetAiDraftState(id, AiDraftState.Open, upTo, 0, now);
            p.Kept++;
            RevisitOutcome("low_confidence");
            MarkRevisited(s, t, upTo, null);
            return null;
        }

        switch (reply.Action)
        {
            case RevisitAction.Keep:
                s.Index.AddRevisit(id, now, AiRevisitOutcome.Keep, 1);
                s.Index.SetAiDraftState(id, AiDraftState.Open, upTo, 0, now);
                p.Kept++;
                RevisitOutcome("keep");
                MarkRevisited(s, t, upTo, null);
                return null;

            case RevisitAction.Replace:
                return await ReplaceAsync(p, s, t, upTo, reply, detail, targetId, now, ct).ConfigureAwait(false);

            default:
                return await RemoveAsync(p, s, t, upTo, now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A failed model call: a failed row, an attempt (the trigger is given up after the third), the
    /// shared backoff and the Ai event. Ends the pass like a drafting failure.</summary>
    private DraftPassEnd? CallFailed(PassState p, RevisitScope s, AiDraftThread t, DateTimeOffset upTo, ModelFailure kind,
        DateTimeOffset now)
    {
        NoteFailedAttempt(p, s, t, upTo, now);
        p.FailedCallsInRow++;
        if (p.Rescan)
        {
            RevisitOutcome("failed");
            return null;
        }
        _consecutiveFailures++;
        var minutes = FailureBackoffMinutes[Math.Min(_consecutiveFailures - 1, FailureBackoffMinutes.Count - 1)];
        _retryAt = now + TimeSpan.FromMinutes(minutes);
        _s.Hub.Event(ActivityKind.Ai, DraftTexts.CallFailed(kind.ToString(), minutes));
        RevisitOutcome("failed");
        return DraftPassEnd.Failed;
    }

    /// <summary>A failed row plus an attempt (no backoff, no event). The third attempt gives the trigger up:
    /// checked moves to upTo and attempts reset.</summary>
    private void NoteFailedAttempt(PassState p, RevisitScope s, AiDraftThread t, DateTimeOffset upTo, DateTimeOffset now)
    {
        var id = t.Draft.DraftEntryId;
        s.Index.AddRevisit(id, now, AiRevisitOutcome.Failed, 1);
        var attempts = t.Draft.Attempts + 1;
        if (attempts >= DraftRevisit.MaxAttempts) s.Index.SetAiDraftState(id, AiDraftState.Open, upTo, 0, now);
        else s.Index.SetAiDraftState(id, AiDraftState.Open, null, attempts, now);
        p.Failed++;
    }

    /// <summary>Step 7: a revisited row for each newer message that has no decision (or only a failed one), except
    /// the replace's target. They don't count in the pass's skipped or row totals.</summary>
    private void MarkRevisited(RevisitScope s, AiDraftThread t, DateTimeOffset upTo, string? exceptId)
    {
        try
        {
            var now = _time.GetUtcNow();
            foreach (var row in s.Index.ThreadInbox(t.ConversationTopic, t.Draft.Checked, upTo, EmailIndex.MaxThreadInboxRows))
            {
                if (exceptId is not null && string.Equals(row.EntryId, exceptId, StringComparison.Ordinal)) continue;
                var prior = s.Index.GetDecision(row.EntryId);
                if (prior is null || prior.Outcome == AiOutcome.Failed)
                    s.Index.UpsertDecision(new AiDecision(row.EntryId, row.InternetMessageId, null, now, AiOutcome.Skipped,
                        DraftRuleIds.Revisited, prior?.ModelCalls ?? 0, prior?.Attempts ?? 0));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: couldn't record a revisit ({Type}).", ex.GetType().Name);
        }
    }

    /// <summary>SPEC 23.6 step 6 "replace": the new draft is saved first, then the old one is moved.</summary>
    private async Task<DraftPassEnd?> ReplaceAsync(PassState p, RevisitScope s, AiDraftThread t, DateTimeOffset upTo,
        RevisitReply reply, EmailDetail detail, string targetId, DateTimeOffset now, CancellationToken ct)
    {
        var id = t.Draft.DraftEntryId;
        var fp = t.Draft.Fingerprint;
        var ai = s.Settings.AiDrafting;
        var tag = s.Key.Length == 0 ? null : s.Key;

        var html = DraftHtml.Sanitize(reply.BodyHtml ?? "");
        if (html.Length == 0) return CallFailed(p, s, t, upTo, ModelFailure.BadOutput, now);

        // Meeting times (SPEC 21.6).
        if (DraftMeetingTimes.IsWanted(ai, new DraftReply(true, reply.Confidence, reply.ReplyAll, reply.BodyHtml, reply.Meeting), html))
        {
            try
            {
                bool europe;
                try
                {
                    var facts = await _s.Mailbox.GetFactsAsync(s.Key, targetId, s.Priority, ct).ConfigureAwait(false);
                    europe = SenderRegion.IsEurope(detail.SenderEmail, facts?.TransportHeaders);
                }
                catch (OutlookUnavailableException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    europe = SenderRegion.IsEurope(detail.SenderEmail, null);
                    _log?.LogDebug("AI drafts: reading the email's headers for meeting times failed ({Type}).", ex.GetType().Name);
                }
                var mt = await _meetings.ApplyAsync(new MeetingTimesRequest(html, reply.Meeting, s.UserSmtp, s.Key,
                    detail.SenderEmail, detail.To, detail.Cc, reply.ReplyAll ?? false, europe, now, s.Settings),
                    s.Priority, ct).ConfigureAwait(false);
                html = mt.Html;
                LogMeetingTimes(mt);
            }
            catch (OutlookUnavailableException)
            {
                NoteFailedAttempt(p, s, t, upTo, now);
                return DraftPassEnd.OutlookUnavailable;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                NoteFailedAttempt(p, s, t, upTo, now);
                return DraftPassEnd.Cancelled;
            }
        }

        // Re-read the old draft: it may have changed while Claude was answering.
        AiDraftInfo? info2 = null;
        var readFailed = false;
        try
        {
            info2 = await _s.Mailbox.ReadAiDraftAsync(s.Key, id, s.Priority, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            s.Index.AddRevisit(id, now, AiRevisitOutcome.Replace, 1);
            return DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            s.Index.AddRevisit(id, now, AiRevisitOutcome.Replace, 1);
            return DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            readFailed = true;
            _log?.LogWarning("AI drafts: reading an AI draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
        }
        var status = readFailed ? ObsoleteDraftStatus.Open : AiDraftGuard.Evaluate(info2, fp);
        if (status != ObsoleteDraftStatus.Movable)
        {
            s.Index.AddRevisit(id, now, AiRevisitOutcome.Replace, 1);
            string outcome;
            switch (status)
            {
                case ObsoleteDraftStatus.Gone or ObsoleteDraftStatus.NotInDrafts or ObsoleteDraftStatus.Sent:
                    s.Index.SetAiDraftState(id, AiDraftState.Gone, null, t.Draft.Attempts, now);
                    outcome = "gone";
                    break;
                case ObsoleteDraftStatus.NotMarked or ObsoleteDraftStatus.Changed:
                    s.Index.SetAiDraftState(id, AiDraftState.Edited, null, t.Draft.Attempts, now);
                    outcome = "edited";
                    break;
                default:
                    outcome = "open";
                    break;
            }
            RevisitOutcome(outcome);
            MarkRevisited(s, t, upTo, null);
            return null;
        }

        // Create the new draft first.
        RepliedDraft made;
        try
        {
            made = await _s.Writer.ReplyAsync(MakePlan(targetId, reply.ReplyAll ?? false, html, s.Priority, ReasonTop(ai, reply.NoteForUser)), ct).ConfigureAwait(false);
        }
        catch (ItemNotFoundException)
        {
            NoteFailedAttempt(p, s, t, upTo, now);
            RevisitOutcome("failed");
            return null;
        }
        catch (OutlookUnavailableException)
        {
            NoteFailedAttempt(p, s, t, upTo, now);
            RevisitOutcome("failed");
            return DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            NoteFailedAttempt(p, s, t, upTo, now);
            RevisitOutcome("failed");
            return DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            NoteFailedAttempt(p, s, t, upTo, now);
            _s.Hub.Event(ActivityKind.Ai, DraftTexts.SaveFailed);
            _log?.LogWarning("AI drafts: saving a reply draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            RevisitOutcome("failed");
            return null;
        }

        // The new draft exists: bookkeeping failures must not stop the old one from being moved.
        var created = _time.GetUtcNow();
        var draftedRow = false;
        try
        {
            var priorT = s.Index.GetDecision(targetId);
            s.Index.UpsertDecision(new AiDecision(targetId, detail.InternetMessageId, null, created, AiOutcome.Drafted, null,
                priorT?.ModelCalls ?? 0, priorT?.Attempts ?? 0, reply.Confidence, made.EntryId, created));
            draftedRow = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: couldn't record a revisit ({Type}).", ex.GetType().Name);
        }
        try
        {
            if (DraftFingerprint.IsValid(made.Fingerprint))
                s.Index.RecordAiDraft(made.EntryId, targetId, created, made.Fingerprint, upTo, created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: couldn't record a revisit ({Type}).", ex.GetType().Name);
        }
        try
        {
            s.Index.AddRevisit(id, created, AiRevisitOutcome.Replace, 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: couldn't record a revisit ({Type}).", ex.GetType().Name);
        }
        p.Updated++;

        // Then move the old one.
        now = _time.GetUtcNow();
        ObsoleteDraftMove? move = null;
        DraftPassEnd? ret = null;
        var moveThrew = false;
        try
        {
            move = await _s.Mailbox.MoveObsoleteAiDraftAsync(s.Key, id, fp, s.Priority, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            moveThrew = true;
            ret = DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            moveThrew = true;
            ret = DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            moveThrew = true;
            _s.Hub.Event(ActivityKind.Ai, RevisitTexts.MoveFailed);
            _log?.LogWarning("AI drafts: moving an outdated draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
        }

        var moved = move?.Status == ObsoleteDraftStatus.Moved;
        if (!moveThrew && move!.Status == ObsoleteDraftStatus.NoDeletedItems)
            _s.Hub.Event(ActivityKind.Ai, RevisitTexts.MoveFailed);
        _s.Hub.Event(ActivityKind.Draft, RevisitTexts.ForReplace(move?.Status, made.Subject),
            mailbox: string.IsNullOrEmpty(tag) ? null : tag);
        s.Index.SetAiDraftState(id, moved ? AiDraftState.Replaced : AiDraftState.Left, upTo, 0, now);
        RevisitOutcome(moved ? "replaced" : "replaced_left");
        MarkRevisited(s, t, upTo, draftedRow ? targetId : null);
        return ret;
    }

    /// <summary>SPEC 23.6 step 6 "remove": the old draft is moved to Deleted Items (by the COM side, behind the guard).</summary>
    private async Task<DraftPassEnd?> RemoveAsync(PassState p, RevisitScope s, AiDraftThread t, DateTimeOffset upTo,
        DateTimeOffset now, CancellationToken ct)
    {
        var id = t.Draft.DraftEntryId;
        var tag = s.Key.Length == 0 ? null : s.Key;

        ObsoleteDraftMove? move = null;
        DraftPassEnd? ret = null;
        var threw = false;
        try
        {
            move = await _s.Mailbox.MoveObsoleteAiDraftAsync(s.Key, id, t.Draft.Fingerprint, s.Priority, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            threw = true;
            ret = DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            threw = true;
            ret = DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            threw = true;
            _s.Hub.Event(ActivityKind.Ai, RevisitTexts.MoveFailed);
            _log?.LogWarning("AI drafts: moving an outdated draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
        }

        s.Index.AddRevisit(id, now, AiRevisitOutcome.Remove, 1);
        string outcome;
        if (threw)
        {
            s.Index.SetAiDraftState(id, AiDraftState.Left, null, t.Draft.Attempts, now);
            outcome = "left";
        }
        else
        {
            switch (move!.Status)
            {
                case ObsoleteDraftStatus.Moved:
                    s.Index.SetAiDraftState(id, AiDraftState.Removed, upTo, 0, now);
                    _s.Hub.Event(ActivityKind.Draft, RevisitTexts.Removed(move.Subject), mailbox: tag);
                    p.Moved++;
                    outcome = "removed";
                    break;
                case ObsoleteDraftStatus.Open:
                    outcome = "open";
                    break;
                case ObsoleteDraftStatus.Changed or ObsoleteDraftStatus.NotMarked:
                    s.Index.SetAiDraftState(id, AiDraftState.Edited, null, t.Draft.Attempts, now);
                    outcome = "edited";
                    break;
                case ObsoleteDraftStatus.Gone or ObsoleteDraftStatus.NotInDrafts or ObsoleteDraftStatus.Sent:
                    s.Index.SetAiDraftState(id, AiDraftState.Gone, null, t.Draft.Attempts, now);
                    outcome = "gone";
                    break;
                default:
                    s.Index.SetAiDraftState(id, AiDraftState.Left, null, t.Draft.Attempts, now);
                    _s.Hub.Event(ActivityKind.Ai, RevisitTexts.MoveFailed);
                    outcome = "left";
                    break;
            }
        }
        RevisitOutcome(outcome);
        MarkRevisited(s, t, upTo, null);
        return ret;
    }
}
