using Xunit;

namespace ClippysRevenge.Core.Tests;

public class ActivityViewTests
{
    private static ActivityEvent Ev(long id, ActivityKind k, string s = "x", string? mb = null, TimeSpan? d = null) =>
        new(id, new DateTimeOffset(2026, 11, 12, 10, 0, 5, TimeSpan.Zero), k, s, d, mb);

    [Fact]
    public void Filter_NewestFirst_NoFilters()
    {
        var r = ActivityView.Filter([Ev(1, ActivityKind.Sync), Ev(2, ActivityKind.Tool)], null, null);
        Assert.Equal([2L, 1L], r.Select(e => e.Id));
    }

    [Fact]
    public void Filter_ByKind()
    {
        var r = ActivityView.Filter([Ev(1, ActivityKind.Sync), Ev(2, ActivityKind.Tool), Ev(3, ActivityKind.Sync)], ActivityKind.Sync, null);
        Assert.Equal([3L, 1L], r.Select(e => e.Id));
    }

    [Fact]
    public void Filter_ByMailbox_CaseInsensitive_AndNoMailbox()
    {
        var evs = new[] { Ev(1, ActivityKind.Sync, mb: "a@x.com"), Ev(2, ActivityKind.Sync), Ev(3, ActivityKind.Tool, mb: "A@X.com") };
        Assert.Equal([3L, 1L], ActivityView.Filter(evs, null, "a@x.com").Select(e => e.Id));
        Assert.Equal([2L], ActivityView.Filter(evs, null, ActivityView.NoMailbox).Select(e => e.Id));
        Assert.Empty(ActivityView.Filter(evs, ActivityKind.Draft, "a@x.com"));
    }

    [Fact]
    public void Mailboxes_DistinctSorted()
    {
        var m = ActivityView.Mailboxes([Ev(1, ActivityKind.Sync, mb: "b@x.com"), Ev(2, ActivityKind.Sync), Ev(3, ActivityKind.Sync, mb: "B@x.com"), Ev(4, ActivityKind.Sync, mb: "a@x.com")]);
        Assert.Equal(["a@x.com", "b@x.com", "—"], m.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(3, m.Count);
    }

    [Fact]
    public void Row_FormatsAndBoldsInvites()
    {
        var r = ActivityView.Row(Ev(1, ActivityKind.Invite, "sent", "a@x.com", TimeSpan.FromMilliseconds(7)), TimeZoneInfo.Utc);
        Assert.Equal("10:00:05", r.Time);
        Assert.Equal("a@x.com", r.Mailbox);
        Assert.Equal("Invite", r.Kind);
        Assert.Equal("7 ms", r.Duration);
        Assert.True(r.Bold);
        Assert.False(ActivityView.Row(Ev(2, ActivityKind.Tool), TimeZoneInfo.Utc).Bold);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(0, "0 ms")]
    [InlineData(1800, "1.8 s")]
    [InlineData(125000, "2 min 5 s")]
    public void FormatDuration(int? ms, string expected) =>
        Assert.Equal(expected, ActivityView.FormatDuration(ms is null ? null : TimeSpan.FromMilliseconds(ms.Value)));

    [Fact]
    public void Header_ProgressOnlyDuringBackfill()
    {
        var b = ActivityView.Header(ActivityState.Backfilling, new ActivityProgress(250, 1000, "Inbox"));
        Assert.True(b.ShowProgress);
        Assert.Equal(25, b.ProgressPercent);
        Assert.Equal("250 of 1,000", b.ProgressText);
        Assert.Contains("Inbox", b.StateText);

        Assert.False(ActivityView.Header(ActivityState.Syncing, new ActivityProgress(5, 10, "x")).ShowProgress);
        Assert.False(ActivityView.Header(ActivityState.Backfilling, new ActivityProgress(0, 0, "")).ShowProgress);
        Assert.Equal("Idle", ActivityView.Header(ActivityState.Idle, ActivityProgress.None).StateText);
    }

    [Fact]
    public void Header_ClampsDone()
    {
        var h = ActivityView.Header(ActivityState.Backfilling, new ActivityProgress(50, 10, ""));
        Assert.Equal(100, h.ProgressPercent);
    }

    [Fact]
    public void CopyText_TabSeparated_NoEmbeddedBreaks()
    {
        var rows = new[] { ActivityView.Row(Ev(1, ActivityKind.Tool, "a\tb\nc", null, TimeSpan.FromMilliseconds(7)), TimeZoneInfo.Utc) };
        Assert.Equal("10:00:05\t—\tTool\ta b c\t7 ms\n", ActivityView.CopyText(rows));
    }
}
