using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>SPEC 23.8 plan (index only). CallsLeft = max(0, Cap - ModelCallsToday()).</summary>
public sealed record RescanPlan(int Emails, int Cap, int CallsLeft, bool ReviseDrafts)
{
    public bool LimitReached => CallsLeft <= 0;

    /// <summary>RescanTexts.Confirm(Emails, CallsLeft, Cap, ReviseDrafts).</summary>
    public string ConfirmText => RescanTexts.Confirm(Emails, CallsLeft, Cap, ReviseDrafts);
}

/// <summary>Emails finished so far / emails in the run (for "Cancel rescan (done/total)").</summary>
public sealed record RescanProgress(int Done, int Total);

/// <param name="Started">False when nothing ran: another rescan is running (Message "") or there is no consent
/// (Message = AiDraftingSettings.ConsentRequiredMessage). Then Stop is null, Processed 0, Counts all 0.</param>
/// <param name="Stop">Null when it ran to the end.</param>
/// <param name="Processed">Emails finished (the "after i" of the stopped text).</param>
/// <param name="Message">The summary text (RescanTexts.Summary or .Stopped); the click-feedback balloon text.</param>
public sealed record RescanResult(bool Started, RescanStop? Stop, int Processed, RescanCounts Counts, int ModelCalls,
    TimeSpan Duration, string Message)
{
    /// <summary>Clicking the balloon shows Drafts (SPEC 23.8): Drafted + Updated &gt; 0.</summary>
    public bool ShowDrafts => Counts.Drafted + Counts.Updated > 0;
}

// The manual "Rescan emails from the last 24 hours" run (SPEC 23.8). Output only ever becomes a saved reply draft
// through IReplyDraftWriter; an old draft is moved (never deleted) only inside the reused RevisitAsync.
// Logs and events never hold subjects, ids, addresses, prompts, model output or the model's reason.
public sealed partial class DraftWorker
{
    private static readonly RescanCounts ZeroCounts = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed record RescanItem(string Key, string MailboxSmtp, DraftCandidate Email);

    private int _rescanning;                       // 0/1, Interlocked
    private CancellationTokenSource? _rescanCts;   // under _lock
    private RescanProgress? _rescanProgress;       // Volatile

    /// <summary>Null when no rescan runs; else (done, total). Thread-safe.</summary>
    public RescanProgress? CurrentRescan => Volatile.Read(ref _rescanProgress);

    /// <summary>Cancels the running rescan (no-op when none). Thread-safe, never throws.</summary>
    public void CancelRescan()
    {
        try
        {
            lock (_lock) _rescanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>SPEC 23.8 plan: index only, no COM, no model call. Never throws (an index that fails counts 0 emails,
    /// Warning). Call off the UI thread (SQLite).</summary>
    public RescanPlan PlanRescan()
    {
        var cur = _s.Settings().Clone();
        var ai = cur.AiDrafting;
        var userSmtp = "";
        try
        {
            userSmtp = _s.Indexes.For(MailboxIndexes.OwnMailbox).GetMeta("mailbox_smtp")?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: reading the mailbox address from the index failed ({Type}).", ex.GetType().Name);
        }
        var n = RescanList(cur, userSmtp, _time.GetUtcNow()).Count;
        return new RescanPlan(n, ai.DailyCap, Math.Max(0, ai.DailyCap - ModelCallsToday()), ai.ReviseDrafts);
    }

    /// <summary>The emails a rescan goes through, oldest first (SPEC 23.8). Shared by the plan and the run.</summary>
    private List<RescanItem> RescanList(Settings cur, string userSmtp, DateTimeOffset now)
    {
        var ai = cur.AiDrafting;
        var since = now - TimeSpan.FromHours(24);
        var open = _s.Indexes.Mailboxes;
        var keys = new List<string> { MailboxIndexes.OwnMailbox };
        foreach (var sm in cur.SharedMailboxes ?? [])
        {
            if (sm is not { Enabled: true, AiDrafts: true }) continue;
            var k = MailboxIndexes.Normalize(sm.Smtp);
            if (k.Length > 0 && !keys.Contains(k, StringComparer.Ordinal) && open.Contains(k, StringComparer.Ordinal)) keys.Add(k);
        }

        var all = new List<RescanItem>();
        foreach (var key in keys)
        {
            var mailboxSmtp = key.Length == 0 ? userSmtp : key;
            var rows = new List<RescanItem>();
            try
            {
                var index = _s.Indexes.For(key);
                foreach (var row in index.InboxSince(since, EmailIndex.MaxRescanRows))
                {
                    if (index.GetDecision(row.EntryId)?.Outcome == AiOutcome.Drafted) continue;
                    var revisitDue = false;
                    if (ai.ReviseDrafts)
                    {
                        var t = index.OpenAiDraftForThread(row.ConversationTopic, row.EventTime);
                        revisitDue = t is not null && t.Draft.Checked < row.EventTime;
                    }
                    if (revisitDue || mailboxSmtp.Length == 0
                        || DraftRules.EvaluateIndex(row, mailboxSmtp, ai, () =>
                            index.RepliedTo(row.InternetMessageId, row.ConversationTopic, row.EventTime)
                            || (key.Length > 0 && _s.Indexes.For(MailboxIndexes.OwnMailbox)
                                .RepliedTo(row.InternetMessageId, row.ConversationTopic, row.EventTime))) is null)
                        rows.Add(new RescanItem(key, mailboxSmtp, row));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.LogWarning("AI drafts: listing emails to rescan failed ({Type}).", ex.GetType().Name);
                rows.Clear();
            }
            all.AddRange(rows);
        }
        // List.Sort isn't stable; OrderBy is.
        return all.OrderBy(i => i.Email.EventTime).ToList();
    }

    private static int Tally(PassState p) =>
        p.Drafted + p.Updated + p.Moved + p.Kept + p.NoReply + p.LowConfidence + p.Skipped + p.Failed;

    private static RescanStop? MapStop(DraftPassEnd end) => end switch
    {
        DraftPassEnd.Cancelled => RescanStop.Cancelled,
        DraftPassEnd.DailyLimit => RescanStop.DailyLimit,
        DraftPassEnd.RateLimited => RescanStop.RateLimited,
        DraftPassEnd.NotSignedIn => RescanStop.NotSignedIn,
        DraftPassEnd.OutlookUnavailable => RescanStop.OutlookUnavailable,
        _ => null,   // the other ends don't occur in a rescan; treated as "finished, go on"
    };

    /// <summary>SPEC 23.8 run. Holds the drafting gate to the end. Never throws (cancellation, incl. ct, ends it with
    /// Stop = Cancelled). Records the summary Ai event and the log line; posts no balloon.</summary>
    public async Task<RescanResult> RescanAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _rescanning, 1, 0) != 0)
            return new RescanResult(false, null, 0, ZeroCounts, 0, TimeSpan.Zero, "");

        var start = _time.GetTimestamp();
        CancellationTokenSource? cts = null;
        var gateHeld = false;
        PassState? p = null;
        try
        {
            if (!_s.Settings().AiDrafting.HasConsent)
                return new RescanResult(false, null, 0, ZeroCounts, 0, TimeSpan.Zero, AiDraftingSettings.ConsentRequiredMessage);

            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_lock) _rescanCts = cts;
            Volatile.Write(ref _rescanProgress, new RescanProgress(0, 0));
            var cancel = cts.Token;

            RescanStop? stop = null;
            var total = 0;
            var done = 0;

            Interlocked.Increment(ref _yield);
            try
            {
                await _gate.WaitAsync(cancel).ConfigureAwait(false);
                gateHeld = true;
            }
            catch (OperationCanceledException)
            {
                stop = RescanStop.Cancelled;
            }
            finally
            {
                Interlocked.Decrement(ref _yield);
            }

            p = new PassState(start) { Rescan = true, Priority = WorkPriority.Interactive, CallToken = cancel };
            if (gateHeld)
            {
                try
                {
                    (stop, total, done) = await RescanRunAsync(p, cancel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stop = RescanStop.Cancelled;
                    done = Math.Max(0, CurrentRescan?.Done ?? 0);
                    total = CurrentRescan?.Total ?? 0;
                }
            }

            var elapsed = _time.GetElapsedTime(start);
            var counts = new RescanCounts(total, p.Drafted, p.Updated, p.Moved, p.Kept, p.NoReply, p.LowConfidence, p.Skipped,
                p.Failed);
            var cap = _s.Settings().AiDrafting.DailyCap;
            var text = stop is { } st ? RescanTexts.Stopped(st, cap, done, counts, elapsed) : RescanTexts.Summary(counts, elapsed);
            _s.Hub.Event(ActivityKind.Ai, text, duration: elapsed);
            _log?.LogInformation("AI drafts rescan: {End}, {Emails} emails, {Drafted} drafted, {Updated} updated, {Moved} moved, {Kept} kept, {NoReply} no reply, {LowConfidence} low confidence, {Skipped} skipped, {Failed} failed, {Calls} calls in {Ms} ms, tokens in {TokensIn} out {TokensOut}{UnknownTokens}",
                stop?.ToString() ?? "Completed", total, p.Drafted, p.Updated, p.Moved, p.Kept, p.NoReply, p.LowConfidence,
                p.Skipped, p.Failed, p.Calls, (long)elapsed.TotalMilliseconds,
                p.Usage.In, p.Usage.Out, AiUsageTexts.UnknownCallsLogDetail(p.Usage.UnknownCalls));
            return new RescanResult(true, stop, done, counts, p.Calls, elapsed, text);
        }
        finally
        {
            p?.Scope?.Dispose();
            if (gateHeld) _gate.Release();
            lock (_lock) _rescanCts = null;
            cts?.Dispose();
            Volatile.Write(ref _rescanProgress, null);
            Volatile.Write(ref _rescanning, 0);
        }
    }

    /// <summary>The run with the gate held: start checks, the list, the loop. Returns the stop, the total and the finished count.</summary>
    private async Task<(RescanStop? Stop, int Total, int Done)> RescanRunAsync(PassState p, CancellationToken cancel,
        CancellationToken ct)
    {
        if (!_s.OutlookAvailable()) return (RescanStop.OutlookUnavailable, 0, 0);

        AiIdentity identity;
        try
        {
            identity = await _s.Mailbox.GetIdentityAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            return (RescanStop.OutlookUnavailable, 0, 0);
        }
        catch (OperationCanceledException)
        {
            return (RescanStop.Cancelled, 0, 0);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: reading the user's identity failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            return (RescanStop.OutlookUnavailable, 0, 0);
        }

        var userSmtp = UserSmtp(identity);
        if (userSmtp.Length == 0)
        {
            _log?.LogWarning("AI drafts: the own mailbox address is unknown; rescan skipped.");
            return (RescanStop.OutlookUnavailable, 0, 0);
        }

        var items = RescanList(_s.Settings().Clone(), userSmtp, _time.GetUtcNow());
        var total = items.Count;
        var done = 0;
        p.Scope = _s.Hub.Begin(ActivityState.Drafting, RescanTexts.ProgressLabel);
        _s.Hub.Report(ActivityState.Drafting, 0, total);
        Volatile.Write(ref _rescanProgress, new RescanProgress(0, total));

        foreach (var it in items)
        {
            if (cancel.IsCancellationRequested) return (RescanStop.Cancelled, total, done);
            var cur = _s.Settings().Clone();
            if (!cur.AiDrafting.HasConsent) return (RescanStop.Cancelled, total, done);

            var before = Tally(p);
            DraftPassEnd? end;
            try
            {
                end = await RescanOneAsync(p, it, cur, identity, userSmtp, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (RescanStop.Cancelled, total, done);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("AI drafts: a rescan step failed ({Type}).", ex.GetType().Name);
                p.Failed++;
                end = null;
            }
            if (end is { } e && MapStop(e) is { } stop) return (stop, total, done);
            if (Tally(p) == before) p.Skipped++;
            done++;
            _s.Hub.Report(ActivityState.Drafting, done, total);
            Volatile.Write(ref _rescanProgress, new RescanProgress(done, total));
            if (p.FailedCallsInRow >= 3) return (RescanStop.Failures, total, done);
        }
        return (null, total, done);
    }

    /// <summary>One email of the rescan. Null = finished, go on; a value stops the rescan.</summary>
    private async Task<DraftPassEnd?> RescanOneAsync(PassState p, RescanItem it, Settings cur, AiIdentity identity,
        string userSmtp, CancellationToken ct)
    {
        if (it.Key.Length > 0 && !SharedAiEnabled(cur, it.Key)) return null;

        var index = _s.Indexes.For(it.Key);
        var e = it.Email;
        if (index.GetDecision(e.EntryId)?.Outcome == AiOutcome.Drafted) return null;

        if (cur.AiDrafting.ReviseDrafts
            && index.OpenAiDraftForThread(e.ConversationTopic, e.EventTime) is { } t
            && t.Draft.Checked < e.EventTime)
        {
            var calls0 = p.Revisits;
            var end = await RevisitAsync(p, new RevisitScope(index, it.Key, it.MailboxSmtp, cur, identity, userSmtp,
                WorkPriority.Interactive), t, e.EventTime, ct).ConfigureAwait(false);
            if (end is not null) return end;
            if (p.Revisits > calls0) return null;
            if (index.GetAiDraft(t.Draft.DraftEntryId)?.State == AiDraftState.Open) return null;
        }

        return await ProcessCandidateAsync(p, index, it.Key, it.MailboxSmtp, e, cur, identity, userSmtp, ct).ConfigureAwait(false);
    }
}
