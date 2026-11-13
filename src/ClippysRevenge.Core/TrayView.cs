using System.Globalization;

namespace ClippysRevenge.Core;

public enum TrayIconKey
{
    Idle, Serving, Busy, Paused, Unavailable, Confirm, Error,
    Backfill0, Backfill1, Backfill2, Backfill3,
}

/// <summary>Sync facts the hub doesn't hold. Supplied by SyncService; Unknown when indexing isn't running.</summary>
public sealed record TrayStats(long? EmailCount, DateTimeOffset? LastSync, DateTimeOffset? NextSync, bool Paused)
{
    public static TrayStats Unknown { get; } = new(null, null, null, false);
}

public sealed record TrayView(
    TrayIconKey Icon,
    bool Animated,
    string Tooltip,
    string StatusLine,
    string SyncLine,
    bool ShowStartOutlook,
    string PauseMenuText);

public static class TrayText
{
    public const int MaxTooltipLength = 127;
    public const string Prefix = "Clippy — ";

    public static string Truncate(string text, int max = MaxTooltipLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 2);
        if (text.Length <= max)
        {
            return text;
        }
        var cut = max - 1;
        if (char.IsHighSurrogate(text[max - 2]))
        {
            cut--;
        }
        return string.Concat(text.AsSpan(0, cut), "…");
    }

    public static string RelativePast(DateTimeOffset then, DateTimeOffset now)
    {
        var d = now - then;
        if (d < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }
        if (d < TimeSpan.FromHours(1))
        {
            return $"{(long)Math.Floor(d.TotalMinutes)} min ago";
        }
        if (d < TimeSpan.FromDays(1))
        {
            return $"{(long)Math.Floor(d.TotalHours)} h ago";
        }
        return $"{(long)Math.Floor(d.TotalDays)} d ago";
    }

    public static string NextIn(DateTimeOffset next, DateTimeOffset now)
    {
        var n = Math.Max(1, (long)Math.Ceiling((next - now).TotalMinutes));
        return $"{n} min";
    }

    public static string McpAddCommand(int port, string token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (token.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            throw new ArgumentException("Token must not contain whitespace or quotes.", nameof(token));
        }
        return $"claude mcp add --scope user --transport http clippy http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/mcp --header \"Authorization: Bearer {token}\"";
    }
}

public sealed class TrayPresenter
{
    public static readonly TimeSpan ServingMinimum = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private DateTimeOffset? _servingSince;
    private string _servingLabel = "";

    public TrayPresenter(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public TrayView Update(ActivityState state, ActivityProgress progress, TrayStats stats, ActivityEvent? lastTool)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var now = _time.GetUtcNow();
        var label = progress.Label ?? "";
        var p = TrayText.Prefix;

        if (state == ActivityState.Serving)
        {
            _servingLabel = label;
            _servingSince ??= now;
        }
        else if (state == ActivityState.Idle)
        {
            if (_servingSince is { } since && now - since < ServingMinimum)
            {
                var text = p + _servingLabel;
                if (lastTool is { Duration: { } dur } && lastTool.Time >= since)
                {
                    text += " (" + ((long)Math.Round(dur.TotalMilliseconds, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " ms)";
                }
                return Build(TrayIconKey.Serving, false, text, text, stats, state, now);
            }
            _servingSince = null;
        }
        else
        {
            _servingSince = null;
        }

        string tip;
        string? status = null;
        var icon = TrayIconKey.Idle;
        var animated = false;
        switch (state)
        {
            case ActivityState.Error:
                icon = TrayIconKey.Error;
                tip = label.Length == 0 ? p + "error" : p + "error: " + label;
                break;
            case ActivityState.AwaitingConfirmation:
                icon = TrayIconKey.Confirm;
                tip = p + "waiting for you to confirm a meeting invite";
                break;
            case ActivityState.OutlookUnavailable:
                icon = TrayIconKey.Unavailable;
                tip = label == OutlookConnection.NotRespondingLabel
                    ? p + "Outlook is not responding"
                    : p + "Outlook is not running";
                break;
            case ActivityState.Backfilling:
                icon = TrayIconKey.Backfill0;
                animated = true;
                tip = BackfillTip(progress);
                break;
            case ActivityState.Syncing:
                icon = TrayIconKey.Busy;
                tip = label.Length == 0 ? p + "pulling new mail" : p + "pulling new mail (" + label + ")";
                break;
            case ActivityState.Purging:
                icon = TrayIconKey.Busy;
                tip = label.Length == 0 ? p + "removing old mail" : p + "removing mail older than " + label;
                break;
            case ActivityState.Drafting:
                {
                    icon = TrayIconKey.Busy;
                    var dt = label.Length == 0 ? ActivityHub.DraftingLabel : label;
                    if (progress.Total > 0)
                    {
                        dt += " (" + progress.Done.ToString("N0", CultureInfo.InvariantCulture) + " / "
                            + progress.Total.ToString("N0", CultureInfo.InvariantCulture) + ")";
                    }
                    tip = p + dt;
                    break;
                }
            case ActivityState.Paused:
                icon = TrayIconKey.Paused;
                tip = p + "indexing paused";
                break;
            case ActivityState.Serving:
                icon = TrayIconKey.Serving;
                tip = label.Length == 0 ? p + "serving" : p + label;
                break;
            default:
                var emails = stats.EmailCount is { } n ? " · " + n.ToString("N0", CultureInfo.InvariantCulture) + " emails" : "";
                status = p + "idle" + emails;
                tip = status + (stats.LastSync is { } ls ? " · synced " + TrayText.RelativePast(ls, now) : "");
                break;
        }
        return Build(icon, animated, tip, status ?? tip, stats, state, now, label);
    }

    private static TrayView Build(TrayIconKey k, bool anim, string tooltip, string statusLine, TrayStats s, ActivityState st, DateTimeOffset now, string label = "")
    {
        var sync = "Last sync: " + (s.LastSync is { } l ? TrayText.RelativePast(l, now) : "never");
        if (s.Paused)
        {
            sync += " · paused";
        }
        else if (s.NextSync is { } nx)
        {
            sync += " · next in " + TrayText.NextIn(nx, now);
        }
        return new TrayView(
            k,
            anim,
            TrayText.Truncate(tooltip),
            TrayText.Truncate(statusLine),
            sync,
            st == ActivityState.OutlookUnavailable && label != OutlookConnection.NotRespondingLabel,
            s.Paused ? "Resume indexing" : "Pause indexing");
    }

    private static string BackfillTip(ActivityProgress progress)
    {
        var label = progress.Label ?? "";
        if (progress.Total <= 0)
        {
            return TrayText.Prefix + "indexing " + label;
        }
        var i = label.IndexOf(" (", StringComparison.Ordinal);
        var what = i < 0 ? label : label[..i];
        var range = i < 0 ? "" : label[i..];
        var counts = progress.Done.ToString("N0", CultureInfo.InvariantCulture) + " / "
            + progress.Total.ToString("N0", CultureInfo.InvariantCulture);
        var head = string.Join(" ", new[] { what, counts }.Where(x => x.Length > 0));
        return TrayText.Prefix + "indexing " + head + range;
    }
}
