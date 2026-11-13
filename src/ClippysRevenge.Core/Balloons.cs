namespace ClippysRevenge.Core;

public enum BalloonKind { Info, Warning, Error }

/// <summary>What a click on the balloon does (SPEC 19.10). Only the AI drafts balloon has one.</summary>
public enum BalloonAction { None, ShowDrafts }

public sealed record Balloon(string Text, BalloonKind Kind, BalloonAction Action = BalloonAction.None);

public static class BalloonTexts
{
    public const string Title = "Clippy's Revenge";
    public const string FirstRun = "Clippy's Revenge is running — right-click to copy the Claude setup command";
    public const string OutlookUnavailable = "It looks like Outlook isn't running. I'll wait — or right-click me and pick Start Outlook.";
    public const string OutlookNotResponding = "It looks like Outlook isn't responding. I'll keep trying — check Outlook for an open dialog box.";
    public const string OutlookNotFound = "It looks like I can't find Outlook on this PC. Start it from the Start menu.";
    public const string Copied = "Copied — paste it in WSL";

    public static string BackfillDone(string what, long count) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"It looks like you're trying to find an email. I finished indexing {what}: {count:N0} emails.");

    public const string NoOutlookWindow = "It looks like Outlook has no open window. Open Outlook and look in Drafts.";

    /// <summary>It looks like you got mail. I drafted 1 reply — they're in your Drafts folder. / … I drafted n replies — …</summary>
    /// <exception cref="ArgumentOutOfRangeException">n is less than 1.</exception>
    public static string AiDrafted(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"It looks like you got mail. I drafted {n} {(n == 1 ? "reply" : "replies")} — they're in your Drafts folder.");
    }

    public static string UpdateAvailable(string v) =>
        $"It looks like there's a newer me: v{v}. Right-click me and pick About Clippy's Revenge… to update.";

    public static string UpdateReady(string v) =>
        $"It looks like there's a newer me: v{v}. I'll install it the next time I start. To do it now, pick About Clippy's Revenge… in my menu.";

    public static string Updated(string v) => $"It looks like I got an upgrade. I'm now v{v}.";

    public static string UpdateRolledBack(string failed, string back) =>
        $"It looks like v{failed} didn't start, so I went back to v{back}. I won't install v{failed} again.";

    public static string Error(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "It looks like something went wrong. Right-click me and pick Activity… for details."
            : $"It looks like something went wrong: {reason}. Right-click me and pick Activity… for details.";
}

public sealed class BalloonCenter
{
    public const int MaxTextLength = 255;
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private DateTimeOffset? _lastAccepted;

    public BalloonCenter(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public event Action<Balloon>? Shown;

    public bool Post(string text, BalloonKind kind = BalloonKind.Info, BalloonAction action = BalloonAction.None)
    {
        var clean = Clean(text);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_lastAccepted is { } last && now - last < MinInterval)
            {
                return false;
            }
            _lastAccepted = now;
        }
        Raise(new Balloon(clean, kind, action));
        return true;
    }

    public void PostUserFeedback(string text) => Raise(new Balloon(Clean(text), BalloonKind.Info));

    private static string Clean(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return TrayText.Truncate(text, MaxTextLength);
    }

    private void Raise(Balloon balloon)
    {
        var handler = Shown;
        if (handler is null)
        {
            return;
        }
        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((Action<Balloon>)d)(balloon);
            }
            catch
            {
                // Subscriber failures must not affect the caller.
            }
        }
    }
}

public sealed class StateBalloonPolicy
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(2);

    private sealed class Episode
    {
        public DateTimeOffset Start;
        public bool Active;
        public bool Notified;
    }

    private readonly TimeProvider _time;
    private readonly Episode _unavailable = new();
    private readonly Episode _error = new();

    public StateBalloonPolicy(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public Balloon? Observe(ActivityState state, ActivityProgress progress, bool notifyOnErrors)
    {
        var now = _time.GetUtcNow();
        var u = Track(_unavailable, state == ActivityState.OutlookUnavailable, now);
        var e = Track(_error, state == ActivityState.Error, now);
        if (u && !_unavailable.Notified && now - _unavailable.Start >= Threshold)
        {
            _unavailable.Notified = true;
            var text = progress.Label == OutlookConnection.NotRespondingLabel
                ? BalloonTexts.OutlookNotResponding
                : BalloonTexts.OutlookUnavailable;
            return new Balloon(text, BalloonKind.Warning);
        }
        if (e && notifyOnErrors && !_error.Notified && now - _error.Start >= Threshold)
        {
            _error.Notified = true;
            return new Balloon(BalloonTexts.Error(progress.Label ?? ""), BalloonKind.Error);
        }
        return null;
    }

    private static bool Track(Episode ep, bool inState, DateTimeOffset now)
    {
        if (!inState)
        {
            ep.Active = false;
            ep.Notified = false;
            return false;
        }
        if (!ep.Active)
        {
            ep.Active = true;
            ep.Notified = false;
            ep.Start = now;
        }
        return true;
    }
}
