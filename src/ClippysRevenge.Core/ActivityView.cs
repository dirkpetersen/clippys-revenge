using System.Globalization;
using System.Text;

namespace ClippysRevenge.Core;

/// <summary>One formatted row of the activity list (SPEC 6.4).</summary>
public sealed record ActivityRow(long Id, string Time, string Mailbox, string Kind, string Summary, string Duration, bool Bold);

/// <summary>The window's top area: state text plus a progress bar that exists only during a backfill.</summary>
public sealed record ActivityHeader(string StateText, bool ShowProgress, int ProgressPercent, string ProgressText);

/// <summary>Pure filtering and formatting for the Activity window (SPEC 6.4). Shows only what the hub recorded.</summary>
public static class ActivityView
{
    /// <summary>Label for events that carry no mailbox.</summary>
    public const string NoMailbox = "—";

    public static string KindName(ActivityKind kind) => kind.ToString();

    public static string StateName(ActivityState state) => state switch
    {
        ActivityState.Idle => "Idle",
        ActivityState.Serving => "Serving a request",
        ActivityState.Paused => "Paused",
        ActivityState.Purging => "Purging old mail",
        ActivityState.Syncing => "Syncing",
        ActivityState.Backfilling => "Indexing your mail",
        ActivityState.OutlookUnavailable => "Waiting for Outlook",
        ActivityState.AwaitingConfirmation => "Waiting for your confirmation",
        ActivityState.Error => "Error",
        _ => state.ToString(),
    };

    public static ActivityHeader Header(ActivityState state, ActivityProgress progress)
    {
        var text = StateName(state);
        var label = progress.Label ?? "";
        if (state != ActivityState.Idle && label.Length > 0)
        {
            text += ": " + label;
        }
        if (state == ActivityState.Backfilling && progress.Total > 0)
        {
            var done = Math.Clamp(progress.Done, 0, progress.Total);
            var pct = (int)(done * 100L / progress.Total);
            return new ActivityHeader(text, true, pct,
                $"{done.ToString("N0", CultureInfo.InvariantCulture)} of {progress.Total.ToString("N0", CultureInfo.InvariantCulture)}");
        }
        return new ActivityHeader(text, false, 0, "");
    }

    /// <summary>Newest first. Null kind or null/empty mailbox means no filter on that axis.</summary>
    public static IReadOnlyList<ActivityEvent> Filter(IReadOnlyList<ActivityEvent> events, ActivityKind? kind, string? mailbox)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = new List<ActivityEvent>();
        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (kind is { } k && e.Kind != k)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(mailbox) && !string.Equals(MailboxLabel(e), mailbox, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(e);
        }
        return result;
    }

    /// <summary>Distinct mailbox labels seen in the events, sorted, for the filter drop-down.</summary>
    public static IReadOnlyList<string> Mailboxes(IReadOnlyList<ActivityEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Select(MailboxLabel).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string MailboxLabel(ActivityEvent e) => string.IsNullOrWhiteSpace(e.Mailbox) ? NoMailbox : e.Mailbox;

    public static ActivityRow Row(ActivityEvent e, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        var local = TimeZoneInfo.ConvertTime(e.Time, zone ?? TimeZoneInfo.Local);
        return new ActivityRow(
            e.Id,
            local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            MailboxLabel(e),
            KindName(e.Kind),
            e.Summary ?? "",
            FormatDuration(e.Duration),
            e.Kind == ActivityKind.Invite);
    }

    public static string FormatDuration(TimeSpan? d)
    {
        if (d is not { } v)
        {
            return "";
        }
        if (v < TimeSpan.FromSeconds(1))
        {
            return ((long)Math.Round(v.TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " ms";
        }
        if (v < TimeSpan.FromMinutes(1))
        {
            return v.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }
        return $"{(long)v.TotalMinutes} min {v.Seconds} s";
    }

    /// <summary>Tab-separated text for the Copy button, one line per row, in the order given.</summary>
    public static string CopyText(IEnumerable<ActivityRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(r.Time).Append('\t').Append(r.Mailbox).Append('\t').Append(r.Kind).Append('\t')
              .Append(Clean(r.Summary)).Append('\t').Append(r.Duration).Append('\n');
        }
        return sb.ToString();
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
