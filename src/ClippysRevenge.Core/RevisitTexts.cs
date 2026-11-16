using System.Globalization;

namespace ClippysRevenge.Core;

/// <summary>SPEC 23.9 exact texts.</summary>
public static class RevisitTexts
{
    public const string MoveFailed =
        "AI drafts: an outdated draft couldn't be moved to Deleted Items. It's still in Drafts.";

    private const string Prefix = "AI reply draft updated (the thread moved on): ";

    public static string UpdatedOldMoved(string? subject, string suffix = "") =>
        Prefix + Short(subject) + (suffix ?? "") + ". The old draft is in Deleted Items.";

    public static string UpdatedOldKept(string? subject, string suffix = "") =>
        Prefix + Short(subject) + (suffix ?? "") + ". The old draft is still in Drafts because it was changed or is open.";

    public static string Updated(string? subject, string suffix = "") => Prefix + Short(subject) + (suffix ?? "") + ".";

    public static string Removed(string? subject) =>
        "Outdated AI reply draft moved to Deleted Items (no reply needed any more): " + Short(subject);

    /// <summary>The Draft event after a replace, from the old draft's move result. oldStatus null = the move threw.</summary>
    public static string ForReplace(ObsoleteDraftStatus? oldStatus, string? newSubject, string suffix = "") => oldStatus switch
    {
        ObsoleteDraftStatus.Moved => UpdatedOldMoved(newSubject, suffix),
        ObsoleteDraftStatus.Open or ObsoleteDraftStatus.Changed or ObsoleteDraftStatus.NotMarked
            or ObsoleteDraftStatus.Movable => UpdatedOldKept(newSubject, suffix),
        _ => Updated(newSubject, suffix),
    };

    /// <summary>Blank gives "(no subject)"; longer than 80 chars gives the first 80 + "…".</summary>
    public static string Short(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "(no subject)";
        return subject.Length > CreateDraftTool.MaxEventSubjectLength
            ? subject[..CreateDraftTool.MaxEventSubjectLength] + "…" : subject;
    }
}

/// <summary>Why a rescan stopped early (SPEC 23.8).</summary>
public enum RescanStop { Cancelled, DailyLimit, RateLimited, NotSignedIn, OutlookUnavailable, Failures }

/// <param name="Emails">n: the planned/total emails.</param>
public sealed record RescanCounts(int Emails, int Drafted, int Updated, int Moved, int Kept, int NoReply,
    int LowConfidence, int Skipped, int Failed);

/// <summary>SPEC 23.8 exact texts.</summary>
public static class RescanTexts
{
    public const string MenuItem = "Rescan emails from the last 24 hours…";
    public const string ConfirmTitle = "Rescan the last 24 hours?";
    public const string ConfirmButton = "Rescan";
    public const string CancelButton = "Cancel";
    public const string NothingToRescan = "There are no emails from the last 24 hours to rescan.";
    public const string ProgressLabel = "Rescanning the last 24 hours";

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string P(int n, string one, string many) => N(n) + " " + (n == 1 ? one : many);

    public static string CancelItem(int done, int total) => "Cancel rescan (" + N(done) + "/" + N(total) + ")";

    public static string Confirm(int emails, int left, int cap, bool reviseDrafts) =>
        "Clippy will go through " + P(emails, "email", "emails")
        + " from the last 24 hours, oldest first, and draft a reply where one is needed. Each email is one Claude call: up to "
        + P(emails, "call", "calls") + ", and " + N(left) + " of today's " + N(cap)
        + " are left. The rescan stops at the daily limit."
        + (reviseDrafts ? " Outdated Clippy drafts in those threads may be replaced or moved to Deleted Items." : "")
        + " Nothing is sent.";

    public static string LimitUsedUp(int cap) => DraftTexts.ManualLimit(cap);

    public static string NotReady(string statusText) => DraftTexts.ManualNotReady(statusText);

    /// <summary>"I couldn't start the rescan: " + reason.</summary>
    public static string StartFailed(string reason) => "I couldn't start the rescan: " + reason;

    /// <exception cref="ArgumentOutOfRangeException">Undefined value.</exception>
    public static string StopReason(RescanStop stop, int cap) => stop switch
    {
        RescanStop.Cancelled => "cancelled",
        RescanStop.DailyLimit => "daily limit of " + N(cap) + " reached",
        RescanStop.RateLimited => "Claude's usage limit was reached",
        RescanStop.NotSignedIn => "Claude isn't signed in",
        RescanStop.OutlookUnavailable => "Outlook isn't running",
        RescanStop.Failures => "3 Claude calls failed in a row",
        _ => throw new ArgumentOutOfRangeException(nameof(stop)),
    };

    /// <exception cref="ArgumentNullException">counts is null.</exception>
    public static string Summary(RescanCounts counts, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return "Rescan: " + P(counts.Emails, "email", "emails") + ", " + Body(counts) + " (" + Dur(duration) + ")";
    }

    /// <exception cref="ArgumentNullException">counts is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">stop undefined.</exception>
    public static string Stopped(RescanStop stop, int cap, int processed, RescanCounts counts, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return "Rescan stopped (" + StopReason(stop, cap) + ") after " + N(processed) + " of "
            + P(counts.Emails, "email", "emails") + ": " + Body(counts) + " (" + Dur(duration) + ")";
    }

    private static string Body(RescanCounts c)
    {
        var s = P(c.Drafted, "draft", "drafts");
        if (c.Updated > 0) s += ", " + P(c.Updated, "draft", "drafts") + " updated";
        if (c.Moved > 0) s += ", " + P(c.Moved, "outdated draft", "outdated drafts") + " moved to Deleted Items";
        if (c.Kept > 0) s += ", " + P(c.Kept, "draft", "drafts") + " still fine";
        if (c.NoReply > 0) s += ", " + N(c.NoReply) + " no reply needed";
        if (c.LowConfidence > 0) s += ", " + N(c.LowConfidence) + " low confidence";
        if (c.Skipped > 0) s += ", " + N(c.Skipped) + " skipped";
        if (c.Failed > 0) s += ", " + N(c.Failed) + " failed";
        return s;
    }

    private static string Dur(TimeSpan d) => d.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
}
