using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>SPEC 27.4. Pure; never logs.</summary>
public static class EditDiff
{
    public const double UnchangedThreshold = 0.98;

    /// <summary>Words (runs of letters, digits and the apostrophes ' and ’) and single other non-whitespace
    /// characters, by Rune (a surrogate pair is one character). Whitespace only separates. Null gives [].</summary>
    public static IReadOnlyList<string> Tokens(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var word = new StringBuilder();
        void EndWord()
        {
            if (word.Length == 0) return;
            result.Add(word.ToString());
            word.Clear();
        }
        foreach (var r in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r) || r.Value == '\'' || r.Value == '’') word.Append(r.ToString());
            else if (Rune.IsWhiteSpace(r)) EndWord();
            else
            {
                EndWord();
                result.Add(r.ToString());
            }
        }
        EndWord();
        return result;
    }

    /// <summary>2.0 * LCS / (draftTokens + sentTokens) over Tokens, ordinal (case-sensitive). Both empty gives 1.0;
    /// exactly one empty gives 0.0. Null counts as "".</summary>
    public static double Similarity(string? draft, string? sent)
    {
        var a = Tokens(draft);
        var b = Tokens(sent);
        int n = a.Count, m = b.Count;
        if (n + m == 0) return 1.0;
        if (n == 0 || m == 0) return 0.0;
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
                cur[j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                    ? prev[j - 1] + 1
                    : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        var lcs = prev[m];
        return 2.0 * lcs / (n + m);
    }

    /// <summary>similarity &gt;= UnchangedThreshold.</summary>
    public static bool IsUnchanged(double similarity) => similarity >= UnchangedThreshold;
}

/// <summary>Texts of SPEC 27 (M20.1 part). Pure.</summary>
public static class EditTexts
{
    /// <summary>AttachmentBudget.Cut(MailText.Redact(StyleCorpus.OwnText(plain, signatureTexts)), EmailIndex.MaxEditTextChars).
    /// Null gives "". Result is at most 1,500 chars.</summary>
    public static string Prepare(string? plain, IReadOnlyList<string>? signatureTexts) =>
        AttachmentBudget.Cut(MailText.Redact(StyleCorpus.OwnText(plain, signatureTexts)), EmailIndex.MaxEditTextChars);

    /// <summary>The one-time notice for users who consented before M20 (SPEC 27.6).</summary>
    public const string Notice = "AI drafts now learn from how you edit Clippy's drafts before sending. The next writing-style update sends up to 20 of your edited replies (addresses, links and phone numbers removed) to Claude. You can turn this off in Settings → AI drafts.";

    /// <summary>"AI drafts: noted how you edited 1 sent draft." / "… &lt;n&gt; sent drafts." (n with N0, invariant).</summary>
    public static string Noted(int n) =>
        "AI drafts: noted how you edited " + n.ToString("N0", CultureInfo.InvariantCulture) +
        (n == 1 ? " sent draft." : " sent drafts.");
}

/// <summary>Ran = false when the run was skipped (feature off, no consent, another run in flight).</summary>
public sealed record EditMatchResult(bool Ran, int Stored, int Unchanged, int Failed)
{
    public static EditMatchResult NotRun { get; } = new(false, 0, 0, 0);
}

/// <summary>SPEC 27.3: matches sent AI drafts to their recorded draft texts. Index only: no COM, no model.
/// Logs and events carry counts and exception type names only, never a text.</summary>
public sealed class EditLearning
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MatchWindow = TimeSpan.FromDays(30);

    private readonly MailboxIndexes _indexes;
    private readonly Func<AiDraftingSettings> _settings;
    private readonly Func<IReadOnlyList<string>> _signatureTexts;
    private readonly ActivityHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastStart;
    private Task<EditMatchResult>? _current;

    /// <param name="signatureTexts">StyleCorpus.SignatureTexts(...) in Program; exceptions count as [].</param>
    /// <exception cref="ArgumentNullException">indexes, settings, signatureTexts or hub is null.</exception>
    public EditLearning(MailboxIndexes indexes, Func<AiDraftingSettings> settings,
        Func<IReadOnlyList<string>> signatureTexts, ActivityHub hub, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(signatureTexts);
        ArgumentNullException.ThrowIfNull(hub);
        _indexes = indexes;
        _settings = settings;
        _signatureTexts = signatureTexts;
        _hub = hub;
        _time = time ?? TimeProvider.System;
        _log = logger;
    }

    /// <summary>SyncService.CycleCompleted hook. Starts MatchAsync on the thread pool when cycle has a result for
    /// MailboxIndexes.OwnMailbox with NotMounted false, LearnFromEdits and HasConsent are true, no run is in flight,
    /// and the last start was at least MinInterval ago (or never). Returns the started task (for tests) or null.
    /// Never blocks, never throws.</summary>
    public Task<EditMatchResult>? OnSyncCycleCompleted(SyncCycleResult? cycle)
    {
        try
        {
            lock (_lock)
            {
                if (cycle is null) return null;
                if (!cycle.Mailboxes.Any(m => m.Mailbox == MailboxIndexes.OwnMailbox && !m.NotMounted)) return null;
                var ai = _settings();
                if (!ai.LearnFromEdits || !ai.HasConsent) return null;
                if (_current is { IsCompleted: false }) return null;
                var now = _time.GetUtcNow();
                if (_lastStart is { } last && now - last < MinInterval) return null;
                _lastStart = now;
                _current = Task.Run(() => MatchAsync(CancellationToken.None));
                return _current;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: starting to learn from edits failed ({Type}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>One match step (see SPEC 27.3). Never throws except OperationCanceledException for ct.</summary>
    public Task<EditMatchResult> MatchAsync(CancellationToken ct = default)
    {
        if (!_gate.Wait(0)) return Task.FromResult(EditMatchResult.NotRun);
        try
        {
            return Task.FromResult(Match(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    private EditMatchResult Match(CancellationToken ct)
    {
        var ai = _settings();
        if (!ai.LearnFromEdits || !ai.HasConsent) return EditMatchResult.NotRun;

        EmailIndex index;
        DateTimeOffset now;
        IReadOnlyList<string> sigs;
        IReadOnlyList<SentAiReply> rows;
        try
        {
            index = _indexes.For(MailboxIndexes.OwnMailbox);
            now = _time.GetUtcNow();
            try { sigs = _signatureTexts() ?? []; }
            catch (Exception) { sigs = []; }
            rows = index.SentAiRepliesToMatch(now - MatchWindow, EmailIndex.MaxSentRepliesToMatch);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log?.LogWarning("AI drafts: learning from edits failed ({Type}).", ex.GetType().Name);
            return new EditMatchResult(true, 0, 0, 0);
        }

        int stored = 0, unchanged = 0, failed = 0;
        string? firstFailure = null;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var cands = index.PendingDraftTexts(row.InReplyTo, row.EventTime);
                if (cands.Count == 0) continue;
                var newest = cands[0];
                var draftText = newest.Text;
                var sentText = EditTexts.Prepare(row.Body, sigs);
                var sim = EditDiff.Similarity(draftText, sentText);
                var same = EditDiff.IsUnchanged(sim);
                var edit = new AiEdit(row.EntryId, row.EventTime, sim, draftText.Length, sentText.Length,
                    same ? null : draftText, same ? null : sentText);
                if (index.StoreEdit(newest.DraftEntryId, row.InReplyTo, edit, now))
                {
                    stored++;
                    if (same) unchanged++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                firstFailure ??= ex.GetType().Name;
            }
        }

        if (failed > 0)
            _log?.LogWarning("AI drafts: couldn't note {Failed} edited drafts ({Type}).", failed, firstFailure);
        if (stored > 0)
        {
            _hub.Event(ActivityKind.Ai, EditTexts.Noted(stored));
            _log?.LogDebug("AI drafts: noted {Stored} sent drafts ({Unchanged} unchanged, {Failed} failed)", stored, unchanged, failed);
        }
        return new EditMatchResult(true, stored, unchanged, failed);
    }
}
