using System.Globalization;

namespace ClippysRevenge.Core.Tests;

public class RevisitTextsTests
{
    private static readonly string Pre = "AI reply draft updated (the thread moved on): ";

    [Fact]
    public void Draft_texts()
    {
        Assert.Equal(Pre + "Re:: Ski. The old draft is in Deleted Items.", RevisitTexts.UpdatedOldMoved("Re:: Ski"));
        Assert.Equal(Pre + "Re:: Ski. The old draft is still in Drafts because it was changed or is open.", RevisitTexts.UpdatedOldKept("Re:: Ski"));
        Assert.Equal(Pre + "Re:: Ski.", RevisitTexts.Updated("Re:: Ski"));
        Assert.Equal("Outdated AI reply draft moved to Deleted Items (no reply needed any more): Re:: Ski", RevisitTexts.Removed("Re:: Ski"));
        Assert.Equal("AI drafts: an outdated draft couldn't be moved to Deleted Items. It's still in Drafts.", RevisitTexts.MoveFailed);
    }

    [Fact]
    public void Short_cuts()
    {
        Assert.Equal(new string('a', 80) + "…", RevisitTexts.Short(new string('a', 81)));
        Assert.Equal(new string('a', 80), RevisitTexts.Short(new string('a', 80)));
        Assert.Equal("(no subject)", RevisitTexts.Short(null));
        Assert.Equal("(no subject)", RevisitTexts.Short(" "));
    }

    [Theory]
    [InlineData(ObsoleteDraftStatus.Moved, 0)]
    [InlineData(ObsoleteDraftStatus.Open, 1)]
    [InlineData(ObsoleteDraftStatus.Changed, 1)]
    [InlineData(ObsoleteDraftStatus.NotMarked, 1)]
    [InlineData(ObsoleteDraftStatus.Movable, 1)]
    [InlineData(ObsoleteDraftStatus.Gone, 2)]
    [InlineData(ObsoleteDraftStatus.NotInDrafts, 2)]
    [InlineData(ObsoleteDraftStatus.Sent, 2)]
    [InlineData(ObsoleteDraftStatus.NoDeletedItems, 2)]
    [InlineData(null, 2)]
    public void For_replace(ObsoleteDraftStatus? status, int kind)
    {
        var expected = kind switch { 0 => RevisitTexts.UpdatedOldMoved("S"), 1 => RevisitTexts.UpdatedOldKept("S"), _ => RevisitTexts.Updated("S") };
        Assert.Equal(expected, RevisitTexts.ForReplace(status, "S"));
    }

    [Fact]
    public void For_replace_inserts_the_suffix_after_the_subject()
    {
        const string suf = " (attachments: 1 read, 0 not read)";
        Assert.Equal("AI reply draft updated (the thread moved on): S" + suf + ". The old draft is in Deleted Items.",
            RevisitTexts.ForReplace(ObsoleteDraftStatus.Moved, "S", suf));
        Assert.Equal("AI reply draft updated (the thread moved on): S" + suf + ". The old draft is still in Drafts because it was changed or is open.",
            RevisitTexts.ForReplace(ObsoleteDraftStatus.Open, "S", suf));
        Assert.Equal("AI reply draft updated (the thread moved on): S" + suf + ".", RevisitTexts.ForReplace(null, "S", suf));
        Assert.Equal(RevisitTexts.ForReplace(ObsoleteDraftStatus.Moved, "S"), RevisitTexts.ForReplace(ObsoleteDraftStatus.Moved, "S", ""));
        Assert.Equal(RevisitTexts.ForReplace(ObsoleteDraftStatus.Moved, "S"), RevisitTexts.ForReplace(ObsoleteDraftStatus.Moved, "S", null!));
        Assert.Equal(RevisitTexts.ForReplace(null, "S"), RevisitTexts.ForReplace(null, "S", null!));
    }

    [Fact]
    public void Confirm_texts()
    {
        Assert.Equal("Clippy will go through 12 emails from the last 24 hours, oldest first, and draft a reply where one is needed. Each email is one Claude call: up to 12 calls, and 25 of today's 30 are left. The rescan stops at the daily limit. Outdated Clippy drafts in those threads may be replaced or moved to Deleted Items. Nothing is sent.",
            RescanTexts.Confirm(12, 25, 30, true));
        Assert.Equal("Clippy will go through 1 email from the last 24 hours, oldest first, and draft a reply where one is needed. Each email is one Claude call: up to 1 call, and 30 of today's 30 are left. The rescan stops at the daily limit. Nothing is sent.",
            RescanTexts.Confirm(1, 30, 30, false));
    }

    [Fact]
    public void Summary_texts()
    {
        Assert.Equal("Rescan: 12 emails, 3 drafts, 1 outdated draft moved to Deleted Items, 8 no reply needed (94 s)",
            RescanTexts.Summary(new(12, 3, 0, 1, 0, 8, 0, 0, 0), TimeSpan.FromSeconds(94)));
        Assert.Equal("Rescan: 1 email, 0 drafts (1.8 s)",
            RescanTexts.Summary(new(1, 0, 0, 0, 0, 0, 0, 0, 0), TimeSpan.FromSeconds(1.84)));
        Assert.Equal("Rescan: 9 emails, 1 draft, 2 drafts updated, 2 outdated drafts moved to Deleted Items, 1 draft still fine, 1 no reply needed, 1 low confidence, 1 skipped, 1 failed (3 s)",
            RescanTexts.Summary(new(9, 1, 2, 2, 1, 1, 1, 1, 1), TimeSpan.FromSeconds(3)));
        Assert.Throws<ArgumentNullException>(() => RescanTexts.Summary(null!, TimeSpan.Zero));
    }

    [Fact]
    public void Stopped_texts()
    {
        Assert.Equal("Rescan stopped (cancelled) after 3 of 12 emails: 1 draft, 2 no reply needed (12.5 s)",
            RescanTexts.Stopped(RescanStop.Cancelled, 30, 3, new(12, 1, 0, 0, 0, 2, 0, 0, 0), TimeSpan.FromSeconds(12.5)));
        Assert.Equal("Rescan stopped (daily limit of 30 reached) after 0 of 1 email: 0 drafts (0 s)",
            RescanTexts.Stopped(RescanStop.DailyLimit, 30, 0, new(1, 0, 0, 0, 0, 0, 0, 0, 0), TimeSpan.Zero));
        Assert.Equal("Claude's usage limit was reached", RescanTexts.StopReason(RescanStop.RateLimited, 30));
        Assert.Equal("Claude isn't signed in", RescanTexts.StopReason(RescanStop.NotSignedIn, 30));
        Assert.Equal("Outlook isn't running", RescanTexts.StopReason(RescanStop.OutlookUnavailable, 30));
        Assert.Equal("3 Claude calls failed in a row", RescanTexts.StopReason(RescanStop.Failures, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => RescanTexts.StopReason((RescanStop)99, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RescanTexts.Stopped((RescanStop)99, 30, 0, new(1, 0, 0, 0, 0, 0, 0, 0, 0), TimeSpan.Zero));
    }

    [Fact]
    public void Misc_texts()
    {
        Assert.Equal("Cancel rescan (3/12)", RescanTexts.CancelItem(3, 12));
        Assert.Equal("Rescan emails from the last 24 hours…", RescanTexts.MenuItem);
        Assert.Equal("Rescan the last 24 hours?", RescanTexts.ConfirmTitle);
        Assert.Equal("Rescan", RescanTexts.ConfirmButton);
        Assert.Equal("Cancel", RescanTexts.CancelButton);
        Assert.Equal("There are no emails from the last 24 hours to rescan.", RescanTexts.NothingToRescan);
        Assert.Equal("Rescanning the last 24 hours", RescanTexts.ProgressLabel);
        Assert.Equal("Today's AI draft limit (30) is used up.", RescanTexts.LimitUsedUp(30));
        Assert.Equal("Claude isn't ready: ✗ x", RescanTexts.NotReady("✗ x"));
    }

    [Fact]
    public void Invariant_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.EndsWith("(1.5 s)", RescanTexts.Summary(new(1, 0, 0, 0, 0, 0, 0, 0, 0), TimeSpan.FromSeconds(1.5)));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Replied_draft_fingerprint()
    {
        var d = new RepliedDraft("D1", "Re:: Budget", "");
        Assert.Null(d.Fingerprint);
        Assert.Equal("fp", (d with { Fingerprint = "fp" }).Fingerprint);
    }
}
