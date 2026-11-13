using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

public sealed record SelectedEmail(string EntryId, string? StoreId);

/// <summary>DisplayName: Session.CurrentUser.Name ("" when unknown). OwnSmtp: the own mailbox's SMTP ("" when unknown).</summary>
public sealed record AiIdentity(string DisplayName, string OwnSmtp);

/// <summary>The COM side of AI drafting (SPEC 19.9). The App implements it in OutlookAiMailbox.</summary>
public interface IAiMailbox
{
    /// <summary>One work item: opens the item, reads class, imid, topic, headers and last verb, and checks the
    /// mailbox's Drafts. Null when the item is gone (ItemNotFoundException).</summary>
    /// <exception cref="OutlookUnavailableException">Outlook is gone or not responding (incl. OutlookNotRespondingException).</exception>
    Task<CandidateFacts?> GetFactsAsync(string mailbox, string entryId, WorkPriority priority, CancellationToken ct);

    /// <exception cref="OutlookUnavailableException"/>
    Task<AiIdentity> GetIdentityAsync(WorkPriority priority, CancellationToken ct);

    /// <summary>Interactive. The first selected MailItem of ActiveExplorer(), or null (no window, no selection, not a MailItem).</summary>
    /// <exception cref="OutlookUnavailableException"/>
    Task<SelectedEmail?> GetSelectedAsync(CancellationToken ct);

    /// <summary>Interactive. The email read through OutlookMail.ReadById (index first, then each enabled mailbox).
    /// Null when it isn't in the own or an enabled shared mailbox (ItemNotFoundException).</summary>
    /// <exception cref="OutlookUnavailableException"/>
    Task<LiveEmail?> ReadEmailAsync(string entryId, CancellationToken ct);

    /// <summary>Interactive. Shows the own Drafts folder in the active Explorer. False when there is no Explorer or
    /// Outlook isn't running. Never throws for those.</summary>
    Task<bool> ShowDraftsAsync(CancellationToken ct);
}

public sealed record DraftWorkerServices(
    Func<Settings> Settings,
    MailboxIndexes Indexes,
    IAiMailbox Mailbox,
    IReplyDraftWriter Writer,
    IDraftModel Model,
    IStyleGuideSource Style,
    Func<string?> ReplySignatureHtml,        // Signatures.Default(SignatureKind.Reply)
    ActivityHub Hub,
    BalloonCenter Balloons,
    Func<bool> OutlookAvailable,             // worker.State == Available
    Func<bool> SyncPaused,                   // sync.IsPaused
    TimeZoneInfo Zone,
    TimeProvider? Time = null,
    ILogger? Logger = null);

public enum DraftPassEnd
{
    Completed, Disabled, Paused, OutlookUnavailable, NotSignedIn, NoIdentity, DailyLimit, Backoff,
    RateLimited, Failed, Yielded, Cancelled,
}

/// <summary>NoReply includes low_confidence. ModelCalls counts calls made in this pass.</summary>
public sealed record DraftPassResult(DraftPassEnd End, int Drafted, int NoReply, int Skipped, int Failed, int ModelCalls);

/// <summary>Today's Claude calls and created drafts (Settings "Today" line).</summary>
public sealed record AiDraftsToday(int Calls, int Drafts);

/// <summary>Message: the click-feedback balloon text (SPEC 19.7 manual).</summary>
public sealed record ManualDraftResult(bool Drafted, string Message);

/// <summary>Exact texts (SPEC 19.7).</summary>
public static class DraftTexts
{
    public const string ManualDone = "It looks like you want a reply. It's in your Drafts folder.";
    public const string ManualNoSelection = "Select one email in Outlook first.";
    public const string ManualNotInMailbox = "That email isn't in your mailbox or an enabled shared mailbox.";
    public const string NotSignedInScope = "AI drafts: Claude isn't signed in";
    public const string SaveFailed = "AI drafts: a reply draft couldn't be saved in Outlook. I'll try again later.";

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public static string ManualLimit(int cap) => Fmt($"Today's AI draft limit ({cap}) is used up.");

    public static string ManualNotReady(string statusText) => "Claude isn't ready: " + statusText;

    public static string ManualFailed(string failure) => "I couldn't draft that reply: " + failure;

    public static string DailyLimit(int cap) => Fmt($"AI drafts: daily limit of {cap} reached. I'll continue tomorrow.");

    public static string RateLimited(string hhmm) => $"AI drafts: Claude's usage limit was reached. I'll try again at {hhmm}.";

    public static string CallFailed(string kind, int minutes) =>
        Fmt($"AI drafts: a Claude call failed ({kind}). I'll try again in {minutes} min.");

    public static string DraftCreated(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "AI reply draft created: (no subject)";
        var s = subject.Length > CreateDraftTool.MaxEventSubjectLength
            ? subject[..CreateDraftTool.MaxEventSubjectLength] + "…" : subject;
        return "AI reply draft created: " + s;
    }

    public static string Summary(int drafted, int noReply, int skipped, int failed, TimeSpan duration)
    {
        var s = Fmt($"AI drafts: {drafted} drafted");
        if (noReply > 0) s += Fmt($", {noReply} no reply needed");
        if (skipped > 0) s += Fmt($", {skipped} skipped");
        if (failed > 0) s += Fmt($", {failed} failed");
        return s + " (" + duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s)";
    }
}

/// <summary>
/// Drafts replies on its own (SPEC 19.7): after each sync cycle, every 15 minutes and when the settings change.
/// Every draft is a saved reply draft made through <see cref="IReplyDraftWriter"/>; nothing is ever sent.
/// Logs and activity events never hold mail text, prompts, style text, model output or addresses (SPEC 19.11).
/// </summary>
public sealed class DraftWorker : BackgroundService
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(15);
    public const int MaxCandidatesPerMailbox = 50;
    public static readonly TimeSpan RateLimitDefaultWait = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RateLimitMargin = TimeSpan.FromMinutes(1);

    /// <summary>Minutes: 1, 2, 4, 8, 16, 32, 60 (then 60 again).</summary>
    public static IReadOnlyList<int> FailureBackoffMinutes { get; } = [1, 2, 4, 8, 16, 32, 60];

    private readonly DraftWorkerServices _s;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _lock = new();

    private DateTimeOffset? _retryAt;
    private int _consecutiveFailures;
    private IDisposable? _notSignedIn;
    private DateOnly? _limitReportedDay;
    private int _yield;     // number of manual drafts waiting for the gate; a running pass yields while it is above 0
    private int _waits;

    /// <exception cref="ArgumentNullException">services or any non-nullable member is null.</exception>
    public DraftWorker(DraftWorkerServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.Settings);
        ArgumentNullException.ThrowIfNull(services.Indexes);
        ArgumentNullException.ThrowIfNull(services.Mailbox);
        ArgumentNullException.ThrowIfNull(services.Writer);
        ArgumentNullException.ThrowIfNull(services.Model);
        ArgumentNullException.ThrowIfNull(services.Style);
        ArgumentNullException.ThrowIfNull(services.ReplySignatureHtml);
        ArgumentNullException.ThrowIfNull(services.Hub);
        ArgumentNullException.ThrowIfNull(services.Balloons);
        ArgumentNullException.ThrowIfNull(services.OutlookAvailable);
        ArgumentNullException.ThrowIfNull(services.SyncPaused);
        ArgumentNullException.ThrowIfNull(services.Zone);
        _s = services;
        _time = services.Time ?? TimeProvider.System;
        _log = services.Logger;
    }

    /// <summary>Test hook: how many times the loop has started waiting for its next pass.</summary>
    internal int WaitCount => Volatile.Read(ref _waits);

    /// <summary>Test hook: manual drafts waiting for the gate.</summary>
    internal int YieldRequests => Volatile.Read(ref _yield);

    /// <summary>Asks for a pass soon (coalesced). Thread-safe, never blocks. Wired to SyncService.CycleCompleted.</summary>
    public void Wake() => _wake.Writer.TryWrite(true);

    /// <summary>Settings were applied (SettingsApplyTargets.AiSettingsChanged): ends a NotSignedIn hold and wakes.</summary>
    public void NotifySettingsChanged()
    {
        ReleaseHold();
        Wake();
    }

    /// <summary>Settings → Check (M11.8) reports the status it got. Ready ends a NotSignedIn hold and wakes.</summary>
    public void ReportStatus(ModelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Ready) NotifySettingsChanged();
    }

    /// <summary>Sum of ModelCallsSince(local midnight) over every open index (for the Settings "Today" line, M11.8).</summary>
    public int ModelCallsToday()
    {
        var start = LocalMidnightUtc(_time.GetUtcNow());
        var total = 0;
        foreach (var key in _s.Indexes.Mailboxes)
        {
            try
            {
                total += _s.Indexes.For(key).ModelCallsSince(start);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("AI drafts: couldn't count today's model calls ({Type}).", ex.GetType().Name);
            }
        }
        return total;
    }

    /// <summary>Calls = ModelCallsToday(). Drafts = sum of DraftsCreatedSince(local midnight) over every open index
    /// (an index that throws counts 0). For the Settings "Today" line.</summary>
    public AiDraftsToday Today()
    {
        var calls = ModelCallsToday();
        var start = LocalMidnightUtc(_time.GetUtcNow());
        var drafts = 0;
        foreach (var key in _s.Indexes.Mailboxes)
        {
            try
            {
                drafts += _s.Indexes.For(key).DraftsCreatedSince(start);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("AI drafts: couldn't count today's drafts ({Type}).", ex.GetType().Name);
            }
        }
        return new AiDraftsToday(calls, drafts);
    }

    private DateTimeOffset LocalMidnightUtc(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, _s.Zone);
        var midnight = localNow.Date;
        if (_s.Zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return new DateTimeOffset(midnight, _s.Zone.GetUtcOffset(midnight));
    }

    // ---------------------------------------------------------------- hold (NotSignedIn)

    private bool Held
    {
        get
        {
            lock (_lock) return _notSignedIn is not null;
        }
    }

    private void ReleaseHold()
    {
        IDisposable? h;
        lock (_lock)
        {
            h = _notSignedIn;
            _notSignedIn = null;
        }
        h?.Dispose();
    }

    /// <summary>Starts the Error scope and records the event, once per hold.</summary>
    private void EnterHold()
    {
        lock (_lock)
        {
            if (_notSignedIn is not null) return;
        }
        var scope = _s.Hub.Begin(ActivityState.Error, DraftTexts.NotSignedInScope);
        var first = false;
        lock (_lock)
        {
            if (_notSignedIn is null)
            {
                _notSignedIn = scope;
                first = true;
            }
        }
        if (first) _s.Hub.Event(ActivityKind.Error, DraftTexts.NotSignedInScope);
        else scope.Dispose();
    }

    // ---------------------------------------------------------------- loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await RunPassAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log?.LogError("AI drafts pass failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            ReleaseHold();
        }
    }

    private async Task WaitAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var timer = Task.Delay(TickInterval, _time, linked.Token);
        var signal = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();
        Interlocked.Increment(ref _waits);
        await Task.WhenAny(timer, signal).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(timer, signal).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        stop.ThrowIfCancellationRequested();
        _wake.Reader.TryRead(out _);
    }

    // ---------------------------------------------------------------- automatic pass

    private sealed class PassState(long start)
    {
        public long Start { get; } = start;
        public IDisposable? Scope { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public int Drafted { get; set; }
        public int NoReply { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Calls { get; set; }
        public int Rows { get; set; }
    }

    /// <summary>One automatic pass, serialized with the loop and the manual path. Exposed for tests.</summary>
    internal async Task<DraftPassResult> RunPassAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        DraftPassResult result;
        try
        {
            result = await PassCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (result.End == DraftPassEnd.Yielded) Wake();
        return result;
    }

    private static DraftPassResult Ended(DraftPassEnd end) => new(end, 0, 0, 0, 0, 0);

    private async Task<DraftPassResult> PassCoreAsync(CancellationToken ct)
    {
        var ai = _s.Settings().AiDrafting;
        if (!ai.Enabled || !ai.HasConsent)
        {
            ReleaseHold();
            return Ended(DraftPassEnd.Disabled);
        }
        if (_s.SyncPaused()) return Ended(DraftPassEnd.Paused);
        if (!_s.OutlookAvailable()) return Ended(DraftPassEnd.OutlookUnavailable);
        if (Held) return Ended(DraftPassEnd.NotSignedIn);

        try
        {
            await _s.Style.BeforePassAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Ended(DraftPassEnd.Cancelled);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: the style guide hook failed ({Type}).", ex.GetType().Name);
        }

        AiIdentity identity;
        try
        {
            identity = await _s.Mailbox.GetIdentityAsync(WorkPriority.Background, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            return Ended(DraftPassEnd.OutlookUnavailable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Ended(DraftPassEnd.Cancelled);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: reading the user's identity failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            return Ended(DraftPassEnd.Failed);
        }

        var userSmtp = UserSmtp(identity);
        if (userSmtp.Length == 0)
        {
            _log?.LogWarning("AI drafts: the own mailbox address is unknown; pass skipped.");
            return Ended(DraftPassEnd.NoIdentity);
        }

        var p = new PassState(_time.GetTimestamp());
        var end = DraftPassEnd.Completed;
        try
        {
            end = await RunMailboxesAsync(p, ai, identity, userSmtp, ct).ConfigureAwait(false);
        }
        finally
        {
            p.Scope?.Dispose();
            p.Scope = null;
            var elapsed = _time.GetElapsedTime(p.Start);
            if (p.Rows > 0)
                _s.Hub.Event(ActivityKind.Ai, DraftTexts.Summary(p.Drafted, p.NoReply, p.Skipped, p.Failed, elapsed), duration: elapsed);
            if (p.Drafted > 0) _s.Balloons.Post(BalloonTexts.AiDrafted(p.Drafted), BalloonKind.Info, BalloonAction.ShowDrafts);
            _log?.LogInformation("AI drafts pass: {End}, {Drafted} drafted, {NoReply} no reply, {Skipped} skipped, {Failed} failed, {Calls} calls in {Ms} ms",
                end, p.Drafted, p.NoReply, p.Skipped, p.Failed, p.Calls, (long)elapsed.TotalMilliseconds);
        }
        return new DraftPassResult(end, p.Drafted, p.NoReply, p.Skipped, p.Failed, p.Calls);
    }

    private string UserSmtp(AiIdentity identity)
    {
        var smtp = identity.OwnSmtp?.Trim() ?? "";
        if (smtp.Length > 0) return smtp;
        try
        {
            return _s.Indexes.For(MailboxIndexes.OwnMailbox).GetMeta("mailbox_smtp")?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: reading the mailbox address from the index failed ({Type}).", ex.GetType().Name);
            return "";
        }
    }

    private static bool SharedAiEnabled(Settings s, string key)
    {
        foreach (var sm in s.SharedMailboxes ?? [])
            if (sm is { Enabled: true, AiDrafts: true } && MailboxIndexes.Normalize(sm.Smtp) == key) return true;
        return false;
    }

    private async Task<DraftPassEnd> RunMailboxesAsync(PassState p, AiDraftingSettings ai, AiIdentity identity,
        string userSmtp, CancellationToken ct)
    {
        var snapshot = _s.Settings().Clone();
        var open = _s.Indexes.Mailboxes;
        var keys = new List<string> { MailboxIndexes.OwnMailbox };
        foreach (var sm in snapshot.SharedMailboxes ?? [])
        {
            if (sm is not { Enabled: true, AiDrafts: true }) continue;
            var k = MailboxIndexes.Normalize(sm.Smtp);
            if (k.Length > 0 && !keys.Contains(k, StringComparer.Ordinal) && open.Contains(k, StringComparer.Ordinal)) keys.Add(k);
        }

        var cutoff = ai.CandidateCutoff(_time.GetUtcNow());
        foreach (var key in keys)
        {
            var index = _s.Indexes.For(key);
            var mailboxSmtp = key.Length == 0 ? userSmtp : key;
            foreach (var c in index.DraftCandidates(cutoff, MaxCandidatesPerMailbox))
            {
                if (ct.IsCancellationRequested) return DraftPassEnd.Cancelled;
                if (Volatile.Read(ref _yield) > 0) return DraftPassEnd.Yielded;
                var cur = _s.Settings().Clone();
                if (!cur.AiDrafting.Enabled || !cur.AiDrafting.HasConsent) return DraftPassEnd.Disabled;
                if (key.Length > 0 && !SharedAiEnabled(cur, key)) break;

                var stop = await ProcessCandidateAsync(p, index, key, mailboxSmtp, c, cur, identity, userSmtp, ct).ConfigureAwait(false);
                if (stop is { } e) return e;
            }
        }
        return DraftPassEnd.Completed;
    }

    private void Write(PassState p, EmailIndex index, DraftCandidate c, string? factsImid, AiDecision prior0, AiOutcome outcome,
        string? rule = null, int calls = 0, int failed = 0, double? confidence = null, string? draftId = null,
        DateTimeOffset? draftCreated = null)
    {
        index.UpsertDecision(new AiDecision(c.EntryId, c.InternetMessageId ?? factsImid, null, _time.GetUtcNow(), outcome, rule,
            prior0.ModelCalls + calls, prior0.Attempts + failed, confidence, draftId, draftCreated));
        p.Rows++;
        switch (outcome)
        {
            case AiOutcome.Drafted: p.Drafted++; break;
            case AiOutcome.NoReply or AiOutcome.LowConfidence: p.NoReply++; break;
            case AiOutcome.Skipped: p.Skipped++; break;
            case AiOutcome.Failed: p.Failed++; break;
        }
    }

    /// <summary>Null = go on with the next candidate.</summary>
    private async Task<DraftPassEnd?> ProcessCandidateAsync(PassState p, EmailIndex index, string key, string mailboxSmtp,
        DraftCandidate c, Settings cur, AiIdentity identity, string userSmtp, CancellationToken ct)
    {
        var ai = cur.AiDrafting;
        var prior = index.GetDecision(c.EntryId) ?? new AiDecision(c.EntryId, null, null, default, AiOutcome.Failed);
        string? factsImid = null;

        // 1. Index rules.
        var rule = DraftRules.EvaluateIndex(c, mailboxSmtp, ai, () =>
            index.RepliedTo(c.InternetMessageId, c.ConversationTopic, c.EventTime)
            || (key.Length > 0 && _s.Indexes.For(MailboxIndexes.OwnMailbox).RepliedTo(c.InternetMessageId, c.ConversationTopic, c.EventTime)));
        if (rule is not null)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Skipped, rule);
            _log?.LogDebug("AI drafts: skipped ({Rule})", rule);
            return null;
        }

        // 2. COM facts.
        CandidateFacts? facts;
        try
        {
            facts = await _s.Mailbox.GetFactsAsync(key, c.EntryId, WorkPriority.Background, ct).ConfigureAwait(false);
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
            _log?.LogWarning("AI drafts: reading a mail's properties failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            return DraftPassEnd.Failed;
        }
        factsImid = facts?.InternetMessageId;
        rule = DraftRules.EvaluateCom(facts);
        if (rule is not null)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Skipped, rule);
            _log?.LogDebug("AI drafts: skipped ({Rule})", rule);
            return null;
        }

        // 3. Daily limit.
        var now = _time.GetUtcNow();
        if (ModelCallsToday() >= ai.DailyCap)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _s.Zone).DateTime);
            if (_limitReportedDay != today)
            {
                _limitReportedDay = today;
                _s.Hub.Event(ActivityKind.Ai, DraftTexts.DailyLimit(ai.DailyCap));
            }
            return DraftPassEnd.DailyLimit;
        }

        // 4. Backoff.
        if (_retryAt is { } retry && retry > now) return DraftPassEnd.Backoff;

        // 5. Model call.
        var detail = index.Get(c.EntryId);
        if (detail is null)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Skipped, DraftRuleIds.Gone);
            return null;
        }
        var email = new PromptEmail(detail.SenderName, detail.SenderEmail, detail.To, detail.Cc, detail.EventTime,
            detail.Subject, NonInlineNames(detail.Attachments), detail.Body);
        var request = BuildRequest(ai, cur, index, key, email, c.ConversationTopic ?? facts!.ConversationTopic, c.EventTime,
            c.EntryId, identity, userSmtp, manual: false);

        p.Scope ??= _s.Hub.Begin(ActivityState.Drafting, ActivityHub.DraftingLabel);
        p.Total++;
        _s.Hub.Report(ActivityState.Drafting, p.Done, p.Total);
        var result = await _s.Model.RunAsync(request, ct).ConfigureAwait(false);
        p.Done++;
        _s.Hub.Report(ActivityState.Drafting, p.Done, p.Total);

        // 6. Result.
        now = _time.GetUtcNow();
        DraftReply? reply = null;
        string html = "";
        ModelFailure? failure = null;
        switch (result)
        {
            case ModelResult.Ok ok:
                p.Calls++;
                reply = DraftReply.Parse(ok.Output);
                if (reply is null)
                {
                    failure = ModelFailure.BadOutput;
                    break;
                }
                if (!reply.NeedsReply)
                {
                    _consecutiveFailures = 0;
                    Write(p, index, c, factsImid, prior, AiOutcome.NoReply, calls: 1, confidence: reply.Confidence);
                    return null;
                }
                if (reply.Confidence < ai.MinConfidence)
                {
                    _consecutiveFailures = 0;
                    Write(p, index, c, factsImid, prior, AiOutcome.LowConfidence, calls: 1, confidence: reply.Confidence);
                    return null;
                }
                html = DraftHtml.Sanitize(reply.BodyHtml);
                if (html.Length == 0)
                {
                    failure = ModelFailure.BadOutput;
                    break;
                }
                _consecutiveFailures = 0;
                break;

            case ModelResult.RateLimited rl:
                _retryAt = rl.RetryAt is { } at && at > now ? at + RateLimitMargin : now + RateLimitDefaultWait;
                _s.Hub.Event(ActivityKind.Ai, DraftTexts.RateLimited(
                    TimeZoneInfo.ConvertTime(_retryAt.Value, _s.Zone).ToString("HH:mm", CultureInfo.InvariantCulture)));
                return DraftPassEnd.RateLimited;

            case ModelResult.NotSignedIn:
                EnterHold();
                return DraftPassEnd.NotSignedIn;

            case ModelResult.Failed { Kind: ModelFailure.Cancelled } when ct.IsCancellationRequested:
                return DraftPassEnd.Cancelled;

            case ModelResult.Failed f:
                p.Calls++;
                failure = f.Kind;
                break;

            default:
                p.Calls++;
                failure = ModelFailure.BadOutput;
                break;
        }

        if (failure is { } kind)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            _consecutiveFailures++;
            var minutes = FailureBackoffMinutes[Math.Min(_consecutiveFailures - 1, FailureBackoffMinutes.Count - 1)];
            _retryAt = now + TimeSpan.FromMinutes(minutes);
            _s.Hub.Event(ActivityKind.Ai, DraftTexts.CallFailed(kind.ToString(), minutes));
            return DraftPassEnd.Failed;
        }

        // 7. Re-check.
        CandidateFacts? facts2;
        try
        {
            facts2 = await _s.Mailbox.GetFactsAsync(key, c.EntryId, WorkPriority.Background, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            return DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            return DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: re-reading a mail's properties failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            return DraftPassEnd.Failed;
        }
        rule = DraftRules.EvaluateBeforeCreate(facts2);
        if (rule is not null)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Skipped, rule, calls: 1, confidence: reply!.Confidence);
            _log?.LogDebug("AI drafts: skipped ({Rule})", rule);
            return null;
        }

        // 8. Create.
        try
        {
            var made = await _s.Writer.ReplyAsync(MakePlan(c.EntryId, reply!.ReplyAll ?? false, html, WorkPriority.Background), ct)
                .ConfigureAwait(false);
            var created = _time.GetUtcNow();
            Write(p, index, c, factsImid, prior, AiOutcome.Drafted, calls: 1, confidence: reply.Confidence,
                draftId: made.EntryId, draftCreated: created);
            _s.Hub.Event(ActivityKind.Draft, DraftTexts.DraftCreated(made.Subject),
                mailbox: string.IsNullOrEmpty(made.Mailbox) ? null : made.Mailbox);
            return null;
        }
        catch (ItemNotFoundException)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Skipped, DraftRuleIds.Gone, calls: 1, confidence: reply!.Confidence);
            return null;
        }
        catch (OutlookUnavailableException)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            return DraftPassEnd.OutlookUnavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            return DraftPassEnd.Cancelled;
        }
        catch (Exception ex)
        {
            Write(p, index, c, factsImid, prior, AiOutcome.Failed, calls: 1, failed: 1);
            _s.Hub.Event(ActivityKind.Ai, DraftTexts.SaveFailed);
            _log?.LogWarning("AI drafts: saving a reply draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
            return null;
        }
    }

    // ---------------------------------------------------------------- shared helpers

    private static IReadOnlyList<string> NonInlineNames(IReadOnlyList<AttachmentInfo> atts) =>
        atts.Where(a => !a.IsInline && !string.IsNullOrWhiteSpace(a.FileName)).Select(a => a.FileName).ToList();

    private ReplyPlan MakePlan(string entryId, bool replyAll, string html, WorkPriority priority)
    {
        var sig = _s.ReplySignatureHtml();
        return new ReplyPlan(entryId, null, replyAll, DraftBody.Fragment(html, html: true, sig),
            DraftBody.SignatureContent(sig) is not null, DraftMarker.Ai, priority);
    }

    private ModelRequest BuildRequest(AiDraftingSettings ai, Settings settings, EmailIndex index, string key, PromptEmail email,
        string? topic, DateTimeOffset eventTime, string entryId, AiIdentity identity, string userSmtp, bool manual)
    {
        var now = _time.GetUtcNow();
        var thread = index.Thread(topic, eventTime, DraftPrompt.MaxThreadMessages, entryId);
        var sig = DraftBody.SignatureContent(_s.ReplySignatureHtml());
        var sigText = sig is null ? null : DraftHtml.ToPlainText(sig);
        var localNow = TimeZoneInfo.ConvertTime(now, _s.Zone);
        var label = TimeZoneLabels.Resolve(settings.TimeZoneLabel, _s.Zone, localNow.DateTime);
        var input = new DraftPromptInput(now, _s.Zone, label, key, sig is not null, thread, email, sigText);
        return new ModelRequest(
            DraftPrompt.SystemPrompt(identity.DisplayName, userSmtp, manual, _s.Style.ReadForPrompt()),
            DraftPrompt.UserPrompt(input), DraftModel.ReplySchema, ai.Model, ai.Effort,
            TimeSpan.FromSeconds(ai.TimeoutSeconds), DraftModel.DraftMaxOutputTokens);
    }

    // ---------------------------------------------------------------- manual path

    /// <summary>The manual path (SPEC 19.7). Never throws except OperationCanceledException for ct.</summary>
    public async Task<ManualDraftResult> DraftSelectedAsync(CancellationToken ct)
    {
        static ManualDraftResult Fail(string text) => new(false, text);
        static ManualDraftResult Failed(string why) => new(false, DraftTexts.ManualFailed(why));

        var ai = _s.Settings().AiDrafting;
        if (!ai.HasConsent) return Fail(AiDraftingSettings.ConsentRequiredMessage);

        SelectedEmail? sel;
        LiveEmail? live;
        try
        {
            sel = await _s.Mailbox.GetSelectedAsync(ct).ConfigureAwait(false);
            if (sel is null) return Fail(DraftTexts.ManualNoSelection);
            live = await _s.Mailbox.ReadEmailAsync(sel.EntryId, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            return Failed("Outlook isn't running");
        }
        if (live is null) return Fail(DraftTexts.ManualNotInMailbox);

        var status = await _s.Model.CheckAsync(ct).ConfigureAwait(false);
        if (!status.Ready) return Fail(DraftTexts.ManualNotReady(status.Text));
        if (ModelCallsToday() >= ai.DailyCap) return Fail(DraftTexts.ManualLimit(ai.DailyCap));

        Interlocked.Increment(ref _yield);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _yield);
            throw;
        }
        Interlocked.Decrement(ref _yield);

        IDisposable? scope = null;
        try
        {
            scope = _s.Hub.Begin(ActivityState.Drafting, ActivityHub.DraftingLabel);
            _s.Hub.Report(ActivityState.Drafting, 0, 1);
            return await ManualCoreAsync(live, ct).ConfigureAwait(false);
        }
        finally
        {
            scope?.Dispose();
            _gate.Release();
        }
    }

    private async Task<ManualDraftResult> ManualCoreAsync(LiveEmail live, CancellationToken ct)
    {
        static ManualDraftResult Failed(string why) => new(false, DraftTexts.ManualFailed(why));

        var cur = _s.Settings().Clone();
        var ai = cur.AiDrafting;

        AiIdentity identity;
        try
        {
            identity = await _s.Mailbox.GetIdentityAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException)
        {
            return Failed("Outlook isn't running");
        }
        var userSmtp = UserSmtp(identity);

        EmailIndex index;
        var key = live.Mailbox ?? "";
        try { index = _s.Indexes.For(key); }
        catch (McpException)
        {
            key = MailboxIndexes.OwnMailbox;
            index = _s.Indexes.For(key);
        }

        var e = live.Email;
        var prior = index.GetDecision(e.EntryId);
        var email = new PromptEmail(e.SenderName, e.SenderEmail, e.To, e.Cc, e.EventTime, e.Subject,
            NonInlineNames(e.Attachments), e.Body);
        var request = BuildRequest(ai, cur, index, key, email, e.ConversationTopic, e.EventTime, e.EntryId, identity, userSmtp, manual: true);

        var result = await _s.Model.RunAsync(request, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        var called = true;
        var text = "";
        RepliedDraft? made = null;
        double? confidence = null;
        switch (result)
        {
            case ModelResult.Ok ok:
                ReleaseHold();
                _consecutiveFailures = 0;
                var reply = DraftReply.Parse(ok.Output);
                if (reply is null)
                {
                    text = DraftTexts.ManualFailed("the Claude call failed (BadOutput)");
                    break;
                }
                confidence = reply.Confidence;
                var html = string.IsNullOrWhiteSpace(reply.BodyHtml) ? "" : DraftHtml.Sanitize(reply.BodyHtml);
                if (html.Length == 0)
                {
                    text = DraftTexts.ManualFailed("Claude didn't write a reply");
                    break;
                }
                try
                {
                    made = await _s.Writer.ReplyAsync(MakePlan(e.EntryId, reply.ReplyAll ?? false, html, WorkPriority.Interactive), ct)
                        .ConfigureAwait(false);
                    text = DraftTexts.ManualDone;
                }
                catch (ItemNotFoundException)
                {
                    text = DraftTexts.ManualFailed("that email is no longer in Outlook");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    WriteManualRow(index, e, prior, null, now, null);
                    throw;
                }
                catch (Exception ex)
                {
                    _log?.LogWarning("AI drafts: saving a reply draft failed ({Type}, HResult {HResult}).", ex.GetType().Name, ex.HResult);
                    text = DraftTexts.ManualFailed("the draft couldn't be saved in Outlook");
                }
                break;

            case ModelResult.RateLimited rl:
                called = false;
                _retryAt = rl.RetryAt is { } at && at > now ? at + RateLimitMargin : now + RateLimitDefaultWait;
                text = DraftTexts.ManualFailed("Claude's usage limit was reached");
                break;

            case ModelResult.NotSignedIn:
                called = false;
                EnterHold();
                text = DraftTexts.ManualFailed("Claude isn't signed in");
                break;

            case ModelResult.Failed { Kind: ModelFailure.Cancelled } when ct.IsCancellationRequested:
                throw new OperationCanceledException(ct);

            case ModelResult.Failed f:
                text = DraftTexts.ManualFailed($"the Claude call failed ({f.Kind})");
                break;

            default:
                text = DraftTexts.ManualFailed("the Claude call failed (BadOutput)");
                break;
        }

        if (made is not null)
        {
            WriteManualRow(index, e, prior, made, now, confidence);
            _s.Hub.Event(ActivityKind.Draft, DraftTexts.DraftCreated(made.Subject),
                mailbox: string.IsNullOrEmpty(made.Mailbox) ? null : made.Mailbox);
            return new ManualDraftResult(true, text);
        }
        if (called) WriteManualRow(index, e, prior, null, now, null);
        return new ManualDraftResult(false, text);
    }

    /// <summary>Counts a manual call toward the daily cap (SPEC 19.7). Drafted gives a drafted row; anything else
    /// bumps the existing row's model_calls, or writes a failed row.</summary>
    private void WriteManualRow(EmailIndex index, IndexedEmail e, AiDecision? prior, RepliedDraft? made, DateTimeOffset now,
        double? confidence)
    {
        if (made is not null)
        {
            index.UpsertDecision(new AiDecision(e.EntryId, e.InternetMessageId, null, now, AiOutcome.Drafted, null,
                (prior?.ModelCalls ?? 0) + 1, prior?.Attempts ?? 0, confidence, made.EntryId, now));
        }
        else if (prior is not null)
        {
            index.UpsertDecision(prior with { ModelCalls = prior.ModelCalls + 1, Decided = now });
        }
        else
        {
            index.UpsertDecision(new AiDecision(e.EntryId, e.InternetMessageId, null, now, AiOutcome.Failed, null, 1, 1));
        }
    }
}
