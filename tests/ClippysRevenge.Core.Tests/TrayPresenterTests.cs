using System.Globalization;

namespace ClippysRevenge.Core.Tests;

public class TrayPresenterTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = T;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly DateTimeOffset T = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static ActivityProgress Lbl(string l, int done = 0, int total = 0) => new(done, total, l);

    private static (TrayPresenter P, ManualTimeProvider C) Make()
    {
        var c = new ManualTimeProvider();
        return (new TrayPresenter(c), c);
    }

    [Fact]
    public void Idle_NoStats()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, null);
        Assert.Equal(TrayIconKey.Idle, v.Icon);
        Assert.False(v.Animated);
        Assert.Equal("Clippy — idle", v.Tooltip);
        Assert.Equal("Clippy — idle", v.StatusLine);
        Assert.Equal("Last sync: never", v.SyncLine);
        Assert.False(v.ShowStartOutlook);
        Assert.Equal("Pause indexing", v.PauseMenuText);
    }

    [Fact]
    public void Idle_WithStats()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Idle, ActivityProgress.None,
            new TrayStats(48210, T.AddMinutes(-2), T.AddMinutes(3), false), null);
        Assert.Equal("Clippy — idle · 48,210 emails · synced 2 min ago", v.Tooltip);
        Assert.Equal("Clippy — idle · 48,210 emails", v.StatusLine);
        Assert.Equal("Last sync: 2 min ago · next in 3 min", v.SyncLine);
    }

    [Fact]
    public void Paused()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Paused, ActivityProgress.None,
            new TrayStats(10, T.AddMinutes(-5), T.AddMinutes(1), true), null);
        Assert.Equal(TrayIconKey.Paused, v.Icon);
        Assert.Equal("Clippy — indexing paused", v.Tooltip);
        Assert.Equal("Last sync: 5 min ago · paused", v.SyncLine);
        Assert.Equal("Resume indexing", v.PauseMenuText);
    }

    [Fact]
    public void Error()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Error, Lbl("Outlook blocked programmatic access"), TrayStats.Unknown, null);
        Assert.Equal(TrayIconKey.Error, v.Icon);
        Assert.Equal("Clippy — error: Outlook blocked programmatic access", v.Tooltip);
        Assert.Equal("Clippy — error", p.Update(ActivityState.Error, Lbl(""), TrayStats.Unknown, null).Tooltip);
    }

    [Fact]
    public void AwaitingConfirmation()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.AwaitingConfirmation, ActivityProgress.None, TrayStats.Unknown, null);
        Assert.Equal("Clippy — waiting for you to confirm a meeting invite", v.Tooltip);
        Assert.Equal(TrayIconKey.Confirm, v.Icon);
    }

    [Fact]
    public void OutlookUnavailable()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.OutlookUnavailable, ActivityProgress.None, TrayStats.Unknown, null);
        Assert.Equal("Clippy — Outlook is not running", v.Tooltip);
        Assert.Equal("Clippy — Outlook is not running", v.StatusLine);
        Assert.Equal(TrayIconKey.Unavailable, v.Icon);
        Assert.True(v.ShowStartOutlook);
    }

    [Fact]
    public void OutlookNotResponding()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.OutlookUnavailable, Lbl("not responding"), TrayStats.Unknown, null);
        Assert.Equal("Clippy — Outlook is not responding", v.Tooltip);
        Assert.Equal("Clippy — Outlook is not responding", v.StatusLine);
        Assert.Equal(TrayIconKey.Unavailable, v.Icon);
        Assert.False(v.ShowStartOutlook);
    }

    [Theory]
    [InlineData(ActivityState.Idle)]
    [InlineData(ActivityState.Serving)]
    [InlineData(ActivityState.Paused)]
    [InlineData(ActivityState.Purging)]
    [InlineData(ActivityState.Syncing)]
    [InlineData(ActivityState.Backfilling)]
    [InlineData(ActivityState.AwaitingConfirmation)]
    [InlineData(ActivityState.Error)]
    public void ShowStartOutlook_FalseElsewhere(ActivityState s)
    {
        var (p, _) = Make();
        Assert.False(p.Update(s, ActivityProgress.None, TrayStats.Unknown, null).ShowStartOutlook);
    }

    [Fact]
    public void Backfilling()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Backfilling, Lbl("Inbox (2025-11 → now)", 1240, 8000), TrayStats.Unknown, null);
        Assert.Equal("Clippy — indexing Inbox 1,240 / 8,000 (2025-11 → now)", v.Tooltip);
        Assert.Equal(TrayIconKey.Backfill0, v.Icon);
        Assert.True(v.Animated);
    }

    [Fact]
    public void Backfilling_SharedMailbox()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Backfilling, Lbl("helpdesk@… Inbox", 300, 2000), TrayStats.Unknown, null);
        Assert.Equal("Clippy — indexing helpdesk@… Inbox 300 / 2,000", v.Tooltip);
    }

    [Fact]
    public void Backfilling_EdgeCases()
    {
        var (p, _) = Make();
        Assert.Equal("Clippy — indexing Inbox",
            p.Update(ActivityState.Backfilling, Lbl("Inbox", 0, 0), TrayStats.Unknown, null).Tooltip);
        Assert.Equal("Clippy — indexing 5 / 10",
            p.Update(ActivityState.Backfilling, Lbl("", 5, 10), TrayStats.Unknown, null).Tooltip);
    }

    [Theory]
    [InlineData("drafting replies", 2, 5, "drafting replies (2 / 5)")]
    [InlineData("drafting replies", 0, 0, "drafting replies")]
    [InlineData("", 0, 0, "drafting replies")]
    [InlineData("drafting replies", 1200, 1500, "drafting replies (1,200 / 1,500)")]
    public void Drafting(string label, int done, int total, string text)
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Drafting, Lbl(label, done, total), TrayStats.Unknown, null);
        Assert.Equal(TrayText.Prefix + text, v.Tooltip);
        Assert.Equal(TrayText.Prefix + text, v.StatusLine);
        Assert.Equal(TrayIconKey.Busy, v.Icon);
        Assert.False(v.Animated);
        Assert.False(v.ShowStartOutlook);
    }

    [Fact]
    public void Syncing()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Syncing, Lbl("Inbox +12"), TrayStats.Unknown, null);
        Assert.Equal("Clippy — pulling new mail (Inbox +12)", v.Tooltip);
        Assert.Equal(TrayIconKey.Busy, v.Icon);
        Assert.Equal("Clippy — pulling new mail", p.Update(ActivityState.Syncing, Lbl(""), TrayStats.Unknown, null).Tooltip);
    }

    [Fact]
    public void Purging()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Purging, Lbl("12 months"), TrayStats.Unknown, null);
        Assert.Equal("Clippy — removing mail older than 12 months", v.Tooltip);
        Assert.Equal(TrayIconKey.Busy, v.Icon);
        Assert.Equal("Clippy — removing old mail", p.Update(ActivityState.Purging, Lbl(""), TrayStats.Unknown, null).Tooltip);
    }

    [Fact]
    public void Serving()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Serving, Lbl("search_emails"), TrayStats.Unknown, null);
        Assert.Equal(TrayIconKey.Serving, v.Icon);
        Assert.Equal("Clippy — search_emails", v.Tooltip);
    }

    private static ActivityEvent Tool(DateTimeOffset time, double ms) =>
        new(1, time, ActivityKind.Tool, "search_emails", TimeSpan.FromMilliseconds(ms), null);

    [Fact]
    public void PulseHold()
    {
        var (p, c) = Make();
        p.Update(ActivityState.Serving, Lbl("search_emails"), TrayStats.Unknown, null);
        c.Advance(TimeSpan.FromMilliseconds(300));
        var v = p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, Tool(T.AddMilliseconds(250), 8));
        Assert.Equal(TrayIconKey.Serving, v.Icon);
        Assert.Equal("Clippy — search_emails (8 ms)", v.Tooltip);
        Assert.Equal("Clippy — search_emails (8 ms)", v.StatusLine);
        c.Advance(TimeSpan.FromMilliseconds(700));
        Assert.Equal(TrayIconKey.Idle, p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, null).Icon);
    }

    [Fact]
    public void HoldWithoutUsableToolEvent()
    {
        foreach (var tool in new ActivityEvent?[] { null, Tool(T.AddMilliseconds(-5), 8) })
        {
            var (p, c) = Make();
            p.Update(ActivityState.Serving, Lbl("search_emails"), TrayStats.Unknown, null);
            c.Advance(TimeSpan.FromMilliseconds(300));
            Assert.Equal("Clippy — search_emails",
                p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, tool).Tooltip);
        }
    }

    [Fact]
    public void PulseYields()
    {
        var (p, c) = Make();
        p.Update(ActivityState.Serving, Lbl("x"), TrayStats.Unknown, null);
        c.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(TrayIconKey.Busy, p.Update(ActivityState.Syncing, Lbl("Inbox"), TrayStats.Unknown, null).Icon);
    }

    [Fact]
    public void LongCall_NoHoldAfterwards()
    {
        var (p, c) = Make();
        p.Update(ActivityState.Serving, Lbl("x"), TrayStats.Unknown, null);
        c.Advance(TimeSpan.FromMilliseconds(1500));
        p.Update(ActivityState.Serving, Lbl("x"), TrayStats.Unknown, null);
        Assert.Equal(TrayIconKey.Idle, p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, null).Icon);
    }

    [Fact]
    public void Overlap_KeepsStartAndNewestLabel()
    {
        var (p, c) = Make();
        p.Update(ActivityState.Serving, Lbl("a"), TrayStats.Unknown, null);
        c.Advance(TimeSpan.FromMilliseconds(100));
        p.Update(ActivityState.Serving, Lbl("b"), TrayStats.Unknown, null);
        c.Advance(TimeSpan.FromMilliseconds(400));
        var v = p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, null);
        Assert.Equal("Clippy — b", v.Tooltip);
        c.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TrayIconKey.Idle, p.Update(ActivityState.Idle, ActivityProgress.None, TrayStats.Unknown, null).Icon);
    }

    [Fact]
    public void Truncate_Cases()
    {
        var s = TrayText.Truncate(new string('a', 200));
        Assert.Equal(127, s.Length);
        Assert.EndsWith("…", s);
        var exact = new string('b', 127);
        Assert.Equal(exact, TrayText.Truncate(exact));

        var pair = new string('c', 125) + "\U0001F600" + new string('d', 3);
        Assert.Equal(130, pair.Length);
        var t = TrayText.Truncate(pair);
        Assert.Equal(126, t.Length);
        Assert.Equal(new string('c', 125) + "…", t);

        Assert.Throws<ArgumentOutOfRangeException>(() => TrayText.Truncate("abc", 1));
        Assert.Throws<ArgumentNullException>(() => TrayText.Truncate(null!));
    }

    [Fact]
    public void LongErrorLabel_IsTruncated()
    {
        var (p, _) = Make();
        var v = p.Update(ActivityState.Error, Lbl(new string('x', 300)), TrayStats.Unknown, null);
        Assert.True(v.Tooltip.Length <= 127);
        Assert.True(v.StatusLine.Length <= 127);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(-5, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    [InlineData(3600, "1 h ago")]
    [InlineData(86340, "23 h ago")]
    [InlineData(86400, "1 d ago")]
    [InlineData(180000, "2 d ago")]
    public void RelativePast(int seconds, string expected) =>
        Assert.Equal(expected, TrayText.RelativePast(T.AddSeconds(-seconds), T));

    [Theory]
    [InlineData(180, "3 min")]
    [InlineData(121, "3 min")]
    [InlineData(10, "1 min")]
    [InlineData(-60, "1 min")]
    public void NextIn(int seconds, string expected) =>
        Assert.Equal(expected, TrayText.NextIn(T.AddSeconds(seconds), T));

    [Fact]
    public void McpAddCommand()
    {
        Assert.Equal(
            "claude mcp add --scope user --transport http clippy http://127.0.0.1:8765/mcp --header \"Authorization: Bearer abc123\"",
            TrayText.McpAddCommand(8765, "abc123"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayText.McpAddCommand(0, "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayText.McpAddCommand(65536, "a"));
        Assert.Throws<ArgumentException>(() => TrayText.McpAddCommand(8765, ""));
        Assert.Throws<ArgumentException>(() => TrayText.McpAddCommand(8765, "a b"));
        Assert.Throws<ArgumentException>(() => TrayText.McpAddCommand(8765, "a\"b"));
    }

    [Fact]
    public void CultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var (p, _) = Make();
            var v = p.Update(ActivityState.Idle, ActivityProgress.None,
                new TrayStats(48210, T.AddMinutes(-2), T.AddMinutes(3), false), null);
            Assert.Contains("48,210", v.Tooltip);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }
}
