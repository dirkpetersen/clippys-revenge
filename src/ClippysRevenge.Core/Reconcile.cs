using System.Globalization;

namespace ClippysRevenge.Core;

/// <summary>One row of Outlook's ListEntryIds table.</summary>
public sealed record OutlookEntry(string EntryId, bool Unread);

/// <summary>One indexed row's reconcile-relevant columns.</summary>
public sealed record IndexedEntry(string EntryId, bool Unread, DateTimeOffset EventTime);

/// <summary>A folder as SyncService knows it: sync_state key (folder EntryID) + configured path.</summary>
public sealed record ReconcileFolder(string FolderKey, string FolderPath);

public sealed record ReconcilePlan(
    IReadOnlyList<string> ToDelete,
    IReadOnlyList<string> ToAdd,
    IReadOnlyList<(string EntryId, bool Unread)> UnreadChanges,
    bool DeletesSuppressed,
    int SuppressedCount);

public sealed record ReconcileResult(
    string FolderPath, bool Ran, int Deleted, int Added, int Skipped, int UnreadUpdated, bool DeletesSuppressed);

/// <summary>The COM side of reconcile. Implemented in App; faked in tests.</summary>
public interface IReconcileSource
{
    /// <summary>Entry id + UnRead for every indexable item in the folder whose event time is &gt;= since.</summary>
    Task<IReadOnlyList<OutlookEntry>> ListEntryIdsAsync(
        string mailbox, ReconcileFolder folder, DateTimeOffset since, CancellationToken ct);

    /// <summary>Reads at most 50 items. Ids that can't be opened or aren't indexable are left out.</summary>
    Task<IReadOnlyList<IndexedEmail>> ReadForIndexAsync(
        string mailbox, ReconcileFolder folder, IReadOnlyList<string> entryIds, CancellationToken ct);
}

public static class ReconcilePlanner
{
    public const int SuppressDeleteThreshold = 20;
    public static readonly TimeSpan EdgeMargin = TimeSpan.FromDays(1);

    public static ReconcilePlan Plan(
        IReadOnlyList<OutlookEntry> outlook, IReadOnlyList<IndexedEntry> indexed, DateTimeOffset cutoff)
    {
        ArgumentNullException.ThrowIfNull(outlook);
        ArgumentNullException.ThrowIfNull(indexed);

        var listed = new Dictionary<string, OutlookEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<OutlookEntry>();
        foreach (var e in outlook)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.EntryId)) continue;
            if (listed.TryAdd(e.EntryId, e)) order.Add(e);
        }

        var stored = new HashSet<string>(indexed.Select(r => r.EntryId), StringComparer.OrdinalIgnoreCase);
        var toAdd = order.Where(e => !stored.Contains(e.EntryId)).Select(e => e.EntryId).ToList();

        var edge = cutoff + EdgeMargin;
        var candidates = indexed
            .Where(r => r.EventTime >= edge && !listed.ContainsKey(r.EntryId))
            .Select(r => r.EntryId).ToList();

        var unread = new List<(string EntryId, bool Unread)>();
        foreach (var r in indexed)
            if (listed.TryGetValue(r.EntryId, out var o) && o.Unread != r.Unread)
                unread.Add((r.EntryId, o.Unread));

        if (order.Count == 0 && candidates.Count >= SuppressDeleteThreshold)
            return new ReconcilePlan([], toAdd, unread, true, candidates.Count);
        return new ReconcilePlan(candidates, toAdd, unread, false, 0);
    }
}

/// <summary>Daily per-folder reconcile (SPEC §10 step 5). Owns sync_state.last_reconcile.</summary>
public sealed class MailboxReconciler
{
    public const int BatchSize = 50;
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IReconcileSource _source;
    private readonly ActivityHub _hub;
    private readonly TimeProvider _time;

    public MailboxReconciler(IReconcileSource source, ActivityHub hub, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(hub);
        _source = source;
        _hub = hub;
        _time = time ?? TimeProvider.System;
    }

    public bool IsDue(SyncState? state) =>
        state is { BackfillComplete: true } s
        && (s.LastReconcile is null || _time.GetUtcNow() - s.LastReconcile.Value >= Interval);

    public async Task<ReconcileResult> ReconcileIfDueAsync(
        string mailbox, EmailIndex index, ReconcileFolder folder, DateTimeOffset cutoff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrEmpty(folder.FolderKey);
        ArgumentException.ThrowIfNullOrEmpty(folder.FolderPath);

        var now = _time.GetUtcNow();
        var path = folder.FolderPath;
        if (!IsDue(index.GetSyncState(folder.FolderKey)))
            return new ReconcileResult(path, false, 0, 0, 0, 0, false);

        var started = _time.GetTimestamp();
        var tag = mailbox.Length == 0 ? null : mailbox;
        using var scope = _hub.Begin(ActivityState.Syncing, $"Reconciling {path}");

        var outlook = await _source.ListEntryIdsAsync(mailbox, folder, cutoff, ct).ConfigureAwait(false);
        var indexed = index.ListEntries(path);
        var plan = ReconcilePlanner.Plan(outlook, indexed, cutoff);

        var unreadUpdated = index.SetUnread(plan.UnreadChanges.Select(c => (c.EntryId, c.Unread)));
        var deleted = index.Delete(plan.ToDelete);

        int added = 0, skipped = 0, done = 0;
        foreach (var chunk in plan.ToAdd.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var read = await _source.ReadForIndexAsync(mailbox, folder, chunk, ct).ConfigureAwait(false);
            var wanted = new HashSet<string>(chunk, StringComparer.OrdinalIgnoreCase);
            var keep = read.Where(e => e is not null && wanted.Contains(e.EntryId) && !string.IsNullOrEmpty(e.Folder)).ToList();
            if (keep.Count > 0) index.UpsertBatch(keep);
            added += keep.Count;
            skipped += chunk.Length - keep.Count;
            done += chunk.Length;
            _hub.Report(done, plan.ToAdd.Count);
        }

        var fresh = index.GetSyncState(folder.FolderKey);
        if (fresh is not null)
            index.SetSyncState(fresh with { LastReconcile = now });

        var duration = _time.GetElapsedTime(started);
        if (deleted + added + unreadUpdated > 0 || skipped > 0)
        {
            var text = $"Reconciled {path}: {Num(deleted)} removed, {Num(added)} added, {Num(unreadUpdated)} read-state updated";
            if (skipped > 0) text += $", {Num(skipped)} unreadable";
            _hub.Event(ActivityKind.Sync, text, duration, tag);
        }
        if (plan.DeletesSuppressed)
            _hub.Event(ActivityKind.Sync,
                $"Reconcile {path}: Outlook listed no items, kept {Num(plan.SuppressedCount)} indexed emails", null, tag);

        return new ReconcileResult(path, true, deleted, added, skipped, unreadUpdated, plan.DeletesSuppressed);
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
