using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>One sync cycle for one mailbox (SPEC §10 steps 1-4 and purge).</summary>
public sealed class SyncEngine
{
    public const int BatchSize = 50;

    private readonly IMailSource _source;
    private readonly ActivityHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly MailboxReconciler? _reconciler;
    private readonly RetentionApplier _retention;

    public SyncEngine(IMailSource source, ActivityHub hub, TimeProvider? time = null, ILogger? logger = null,
        IReconcileSource? reconcile = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(hub);
        _source = source;
        _hub = hub;
        _time = time ?? TimeProvider.System;
        _log = logger;
        _reconciler = reconcile is null ? null : new MailboxReconciler(reconcile, hub, _time);
        _retention = new RetentionApplier(hub, _time);
    }

    /// <summary>Applies a changed RetentionMonths (needs no COM). Callers must not run it concurrently with a cycle.</summary>
    public RetentionChange ApplyRetention(MailboxPlan plan, EmailIndex index)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(index);
        return _retention.Apply(plan.Mailbox, index, Math.Clamp(plan.RetentionMonths, 1, 120));
    }

    private sealed class FolderRun(string path)
    {
        public string Path { get; } = path;
        public int Upserted { get; set; }
        public int UnreadUpdated { get; set; }
        public bool Complete { get; set; }
        public bool StartedIncomplete { get; set; }
    }

    /// <summary>
    /// One §10 cycle for one mailbox. Throws OperationCanceledException (ct), MailSourceUnavailableException,
    /// and anything the index or source throws; every ActivityHub scope it opened is disposed first.
    /// </summary>
    public async Task<MailboxSyncResult> SyncMailboxAsync(MailboxPlan plan, EmailIndex index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(index);

        var now = OutlookDates.TruncateToSecond(_time.GetUtcNow());
        var cutoff = now.AddMonths(-plan.RetentionMonths);
        var prefix = SyncLabels.MailboxPrefix(plan.Mailbox);
        var started = _time.GetTimestamp();

        ApplyRetention(plan, index);

        var mb = await _source.OpenMailboxAsync(plan.Mailbox, ct).ConfigureAwait(false);
        if (mb is null)
            return new MailboxSyncResult(plan.Mailbox, true, [], 0, false, _time.GetElapsedTime(started));
        index.SetMeta("mailbox_smtp", mb.Smtp);
        index.SetMeta("store_id", mb.StoreId);

        var results = new List<FolderSyncResult>();
        var runs = new List<FolderRun>();
        foreach (var path in plan.Folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var f = await _source.GetFolderAsync(mb, path, ct).ConfigureAwait(false);
            if (f is null)
            {
                results.Add(new FolderSyncResult(path, true, 0, 0, false));
                continue;
            }

            var state = index.GetSyncState(f.FolderKey);
            var run = new FolderRun(path);
            if (state is null)
            {
                state = new SyncState(f.FolderKey, path, now, now, null, false);
                index.SetSyncState(state);
                run.StartedIncomplete = true;
            }
            else
            {
                run.StartedIncomplete = !state.BackfillComplete;
                if (state.FolderPath != path)
                {
                    state = state with { FolderPath = path };
                    index.SetSyncState(state);
                }
            }

            state = await ChangesPassAsync(plan, prefix, f, path, state, index, cutoff, run, ct).ConfigureAwait(false);
            await BackfillPassAsync(plan, prefix, f, path, state, index, cutoff, now, run, ct).ConfigureAwait(false);
            await ReconcileAsync(plan, f, path, index, cutoff, ct).ConfigureAwait(false);
            runs.Add(run);
            results.Add(new FolderSyncResult(path, false, run.Upserted, run.UnreadUpdated, run.Complete));
        }

        var purged = 0;
        var cutoffLocal = TimeZoneInfo.ConvertTime(cutoff, _time.LocalTimeZone);
        if (index.Status().Oldest is { } oldest && oldest < cutoff)
        {
            using (_hub.Begin(ActivityState.Purging, Num(plan.RetentionMonths) + " months"))
                purged = index.PurgeOlderThan(cutoff);
            if (purged > 0)
                _hub.Event(ActivityKind.Purge,
                    $"removed {purged.ToString("N0", CultureInfo.InvariantCulture)} emails older than {cutoffLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
                    mailbox: plan.Mailbox);
        }

        index.SetMeta("retention_months", Num(plan.RetentionMonths));
        index.SetMeta("last_sync", Num(_time.GetUtcNow().ToUnixTimeSeconds()));

        var elapsed = _time.GetElapsedTime(started);
        if (runs.Any(r => r.Upserted + r.UnreadUpdated > 0) || purged > 0)
        {
            var parts = string.Join(", ", runs.Select(r => $"{SyncLabels.FolderLabel(r.Path)} +{Num(r.Upserted)}"));
            _hub.Event(ActivityKind.Sync,
                $"{parts} ({elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)",
                elapsed, plan.Mailbox);
        }

        var finished = runs.Count > 0 && runs.All(r => r.Complete) && runs.Any(r => r.StartedIncomplete);
        _log?.LogDebug("Sync of mailbox {Mailbox} done: {Folders} folders, {Purged} purged.",
            plan.Mailbox.Length == 0 ? "(own)" : plan.Mailbox, runs.Count, purged);
        return new MailboxSyncResult(plan.Mailbox, false, results, purged, finished, elapsed);
    }

    /// <summary>Step 5. A source failure is logged and the next folder carries on.</summary>
    private async Task ReconcileAsync(MailboxPlan plan, SourceFolder f, string path, EmailIndex index,
        DateTimeOffset cutoff, CancellationToken ct)
    {
        if (_reconciler is null) return;
        try
        {
            await _reconciler.ReconcileIfDueAsync(plan.Mailbox, index, new ReconcileFolder(f.FolderKey, path), cutoff, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailSourceUnavailableException)
        {
            _log?.LogWarning(ex, "Reconcile of folder {Folder} failed.", path);
            _hub.Event(ActivityKind.Error, $"reconcile of \"{path}\" failed: " + (ex.Message.Length <= 200 ? ex.Message : ex.Message[..200]),
                mailbox: plan.Mailbox.Length == 0 ? null : plan.Mailbox);
        }
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Step 3. Returns the updated state.</summary>
    private async Task<SyncState> ChangesPassAsync(MailboxPlan plan, string prefix, SourceFolder f, string path,
        SyncState state, EmailIndex index, DateTimeOffset cutoff, FolderRun run, CancellationToken ct)
    {
        var since = state.ModifiedWatermark ?? cutoff;
        var listed = await _source.ListChangedAsync(f, since, cutoff, ct).ConfigureAwait(false);
        var kept = Distinct(listed.Where(r => r.EventTime >= cutoff));
        var (toRead, unread) = Classify(kept, index);
        toRead.Sort(static (a, b) =>
        {
            var c = Nullable.Compare(a.LastModified, b.LastModified);
            return c != 0 ? c : string.CompareOrdinal(a.EntryId, b.EntryId);
        });

        var watermark = state.ModifiedWatermark;
        if (toRead.Count == 0 && unread.Count == 0)
        {
            return AdvanceWatermark(state, index, kept, ref watermark);
        }

        var label = (prefix.Length == 0 ? "" : prefix + " ") + SyncLabels.FolderLabel(path) + " +" + Num(toRead.Count);
        using var scope = toRead.Count > 0 ? _hub.Begin(ActivityState.Syncing, label) : null;

        run.UnreadUpdated += index.SetUnread(unread);
        foreach (var batch in toRead.Chunk(BatchSize))
        {
            var emails = await _source.ReadAsync(f, batch.Select(r => r.EntryId).ToList(), ct).ConfigureAwait(false);
            index.UpsertBatch(emails.Select(e => e with { Folder = path }).ToList());
            run.Upserted += emails.Count;
            var max = batch.Max(r => r.LastModified);
            if (max is { } m && (watermark is null || m > watermark))
            {
                watermark = m;
                state = state with { ModifiedWatermark = m };
                index.SetSyncState(state);
            }
            ct.ThrowIfCancellationRequested();
        }
        return AdvanceWatermark(state, index, kept, ref watermark);
    }

    private static SyncState AdvanceWatermark(SyncState state, EmailIndex index, IReadOnlyList<MailRow> kept, ref DateTimeOffset? watermark)
    {
        var max = kept.Max(r => r.LastModified);
        if (max is { } m && (watermark is null || m > watermark))
        {
            watermark = m;
            state = state with { ModifiedWatermark = m };
            index.SetSyncState(state);
        }
        return state;
    }

    /// <summary>Step 4.</summary>
    private async Task BackfillPassAsync(MailboxPlan plan, string prefix, SourceFolder f, string path,
        SyncState state, EmailIndex index, DateTimeOffset cutoff, DateTimeOffset now, FolderRun run, CancellationToken ct)
    {
        var oldest = state.OldestSynced ?? now;
        if (oldest <= cutoff)
        {
            if (!state.BackfillComplete)
            {
                state = state with { BackfillComplete = true };
                index.SetSyncState(state);
            }
            run.Complete = true;
            return;
        }

        if (state.BackfillComplete)
        {
            state = state with { BackfillComplete = false };
            index.SetSyncState(state);
        }

        var cutoffLocal = TimeZoneInfo.ConvertTime(cutoff, _time.LocalTimeZone)
            .ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var folderLabel = SyncLabels.FolderLabel(path);
        var scopeLabel = (prefix.Length == 0 ? "" : prefix + " ") + $"{folderLabel} ({cutoffLocal} → now)";
        using var scope = _hub.Begin(ActivityState.Backfilling, scopeLabel);

        var listed = await _source.ListRangeAsync(f, cutoff, oldest, ct).ConfigureAwait(false);
        var rows = Distinct(listed.Where(r => r.EventTime >= cutoff)).ToList();
        rows.Sort(static (a, b) =>
        {
            var c = b.EventTime.CompareTo(a.EventTime);
            return c != 0 ? c : string.CompareOrdinal(a.EntryId, b.EntryId);
        });
        var total = rows.Count;
        if (total > 0)
            _hub.Event(ActivityKind.Backfill,
                $"{folderLabel}: backfill started, {total.ToString("N0", CultureInfo.InvariantCulture)} emails to index ({cutoffLocal} → now)",
                mailbox: plan.Mailbox);

        var done = 0;
        _hub.Report(done, total);
        foreach (var batch in rows.Chunk(BatchSize))
        {
            var (toRead, unread) = Classify(batch, index);
            if (toRead.Count > 0)
            {
                var emails = await _source.ReadAsync(f, toRead.Select(r => r.EntryId).ToList(), ct).ConfigureAwait(false);
                index.UpsertBatch(emails.Select(e => e with { Folder = path }).ToList());
                run.Upserted += emails.Count;
            }
            run.UnreadUpdated += index.SetUnread(unread);

            state = state with { OldestSynced = batch[^1].EventTime };
            index.SetSyncState(state);
            done += batch.Length;
            _hub.Report(done, total);
            ct.ThrowIfCancellationRequested();
        }

        state = state with { OldestSynced = cutoff, BackfillComplete = true };
        index.SetSyncState(state);
        run.Complete = true;
        if (total > 0)
            _hub.Event(ActivityKind.Backfill, $"{folderLabel}: backfill complete", mailbox: plan.Mailbox);
    }

    private static List<MailRow> Distinct(IEnumerable<MailRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MailRow>();
        foreach (var r in rows)
            if (!string.IsNullOrEmpty(r.EntryId) && seen.Add(r.EntryId)) result.Add(r);
        return result;
    }

    /// <summary>Skip rule: rows to read, and unread-only updates.</summary>
    private static (List<MailRow> ToRead, List<(string, bool)> Unread) Classify(IReadOnlyList<MailRow> rows, EmailIndex index)
    {
        var toRead = new List<MailRow>();
        var unread = new List<(string, bool)>();
        if (rows.Count == 0) return (toRead, unread);
        var info = index.GetSyncInfo(rows.Select(r => r.EntryId).ToList());
        foreach (var r in rows)
        {
            if (!info.TryGetValue(r.EntryId, out var stored)
                || stored.LastModified is null
                || r.LastModified is null
                || stored.LastModified.Value.ToUnixTimeSeconds() != r.LastModified.Value.ToUnixTimeSeconds())
            {
                toRead.Add(r);
            }
            else if (stored.Unread != r.Unread)
            {
                unread.Add((r.EntryId, r.Unread));
            }
        }
        return (toRead, unread);
    }
}
