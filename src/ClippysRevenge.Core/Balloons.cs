namespace ClippysRevenge.Core;

public enum BalloonKind { Info, Warning, Error }

public sealed record Balloon(string Text, BalloonKind Kind);

public static class BalloonTexts
{
    public const string Title = "Clippy's Revenge";
    public const string FirstRun = "Clippy's Revenge is running — right-click to copy the Claude setup command";
    public const string OutlookUnavailable = "It looks like Outlook isn't running. I'll wait — or right-click me and pick Start Outlook.";
    public const string Copied = "Copied — paste it in WSL";

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

    public bool Post(string text, BalloonKind kind = BalloonKind.Info)
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
        Raise(new Balloon(clean, kind));
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
            return new Balloon(BalloonTexts.OutlookUnavailable, BalloonKind.Warning);
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
