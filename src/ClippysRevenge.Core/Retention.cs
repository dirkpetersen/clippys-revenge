using System.Globalization;

namespace ClippysRevenge.Core;

public static class Retention
{
    /// <summary>now.ToUniversalTime().AddMonths(-months). Throws ArgumentOutOfRangeException outside 1-120.</summary>
    public static DateTimeOffset Cutoff(DateTimeOffset now, int months)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(months, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(months, 120);
        return now.ToUniversalTime().AddMonths(-months);
    }
}

public enum RetentionChangeKind { None, Recorded, Grew, Shrank }

public sealed record RetentionChange(
    RetentionChangeKind Kind, int? PreviousMonths, int Months,
    int Purged, int FoldersReopened);

/// <summary>Applies a changed RetentionMonths to one mailbox index (SPEC §10). Meta is written last.</summary>
public sealed class RetentionApplier
{
    public const string MetaKey = "retention_months";

    private readonly ActivityHub _hub;
    private readonly TimeProvider _time;

    public RetentionApplier(ActivityHub hub, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        _time = time ?? TimeProvider.System;
    }

    public RetentionChange Apply(string mailbox, EmailIndex index, int effectiveMonths)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveMonths, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effectiveMonths, 120);

        var started = _time.GetTimestamp();
        var cutoff = Retention.Cutoff(_time.GetUtcNow(), effectiveMonths);
        var tag = string.IsNullOrEmpty(mailbox) ? null : mailbox;
        var value = effectiveMonths.ToString(CultureInfo.InvariantCulture);

        int? previous = int.TryParse(index.GetMeta(MetaKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
        if (previous is null)
        {
            index.SetMeta(MetaKey, value);
            return new RetentionChange(RetentionChangeKind.Recorded, null, effectiveMonths, 0, 0);
        }
        if (previous == effectiveMonths)
            return new RetentionChange(RetentionChangeKind.None, previous, effectiveMonths, 0, 0);

        if (effectiveMonths > previous)
        {
            var reopened = 0;
            foreach (var s in index.ListSyncStates())
            {
                if (s.OldestSynced is { } o && o > cutoff)
                {
                    index.SetSyncState(s with { BackfillComplete = false });
                    reopened++;
                }
            }
            index.SetMeta(MetaKey, value);
            _hub.Event(ActivityKind.Sync, $"Retention changed to {value} months: backfilling older mail", null, tag);
            return new RetentionChange(RetentionChangeKind.Grew, previous, effectiveMonths, 0, reopened);
        }

        foreach (var s in index.ListSyncStates())
        {
            if (s.OldestSynced is { } o && o < cutoff)
                index.SetSyncState(s with { OldestSynced = cutoff });
        }
        int purged;
        using (_hub.Begin(ActivityState.Purging, "Purging"))
            purged = index.PurgeOlderThan(cutoff);
        index.SetMeta(MetaKey, value);
        _hub.Event(ActivityKind.Purge, $"Retention changed to {value} months: purged {purged.ToString(CultureInfo.InvariantCulture)} emails",
            _time.GetElapsedTime(started), tag);
        return new RetentionChange(RetentionChangeKind.Shrank, previous, effectiveMonths, purged, 0);
    }
}
