using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class DraftInviteMenuTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("T", TimeSpan.FromHours(-8), "T", "T");
    private static readonly DateTimeOffset Created = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);

    private static DraftInviteEntry E(string id, string mailbox = "", DateTimeOffset? createdUtc = null) =>
        new(id, "S", mailbox, createdUtc ?? Created);

    private static PendingDraftInvite P(DraftInviteEntry e, string subject, DateTime start, DateTime end) =>
        new(e, new DraftInviteDetails(subject, start, end));

    private static PendingDraftInvite P(DraftInviteEntry e) => new(e, null);

    private static DateTime D(int day, int h, int m = 0) => new(2026, 11, day, h, m, 0, DateTimeKind.Unspecified);

    private static DraftInviteMenuModel Build(IReadOnlyList<PendingDraftInvite> p, OutlookConnectionState s = OutlookConnectionState.Available,
        IEnumerable<SharedMailbox?>? shared = null) => DraftInviteMenu.Build(p, s, shared ?? [], Tz);

    [Fact]
    public void Empty_is_hidden()
    {
        var m = Build([]);
        Assert.False(m.Visible);
        Assert.Empty(m.Lines);
        Assert.False(m.StartOutlookHint);
    }

    [Fact]
    public void One_line_format()
    {
        var e = E("a");
        var m = Build([P(e, "Project kickoff", D(18, 10), D(18, 11))]);
        var line = Assert.Single(m.Lines);
        Assert.Equal("Wed 11/18 10:00–11:00  Project kickoff", line.Label);
        Assert.Equal(line.Label, line.Text);
        Assert.Same(e, line.Entry);
        Assert.Equal("Draft invites (1)", m.Header);
        Assert.True(m.Visible);
    }

    [Fact]
    public void Sorted_by_start_then_no_details()
    {
        var m = Build([
            P(E("d19"), "x", D(19, 9), D(19, 10)),
            P(E("d17"), "x", D(17, 15), D(17, 16)),
            P(E("n17", createdUtc: new DateTimeOffset(2026, 11, 14, 17, 0, 0, TimeSpan.Zero))),
            P(E("d18"), "x", D(18, 8), D(18, 9)),
            P(E("n16", createdUtc: new DateTimeOffset(2026, 11, 14, 16, 0, 0, TimeSpan.Zero))),
        ]);
        Assert.Equal(["d17", "d18", "d19", "n16", "n17"], m.Lines.Select(l => l.Entry!.EntryId).ToArray());
    }

    [Fact]
    public void Same_start_orders_by_end_then_input()
    {
        var m = Build([
            P(E("a"), "x", D(18, 10), D(18, 10, 30)),
            P(E("b"), "x", D(18, 10), D(18, 10, 15)),
            P(E("c"), "x", D(18, 10), D(18, 10, 15)),
        ]);
        Assert.Equal(["b", "c", "a"], m.Lines.Select(l => l.Entry!.EntryId).ToArray());
    }

    [Fact]
    public void Subject_cut_to_40()
    {
        Assert.EndsWith("  " + new string('a', 40), Build([P(E("a"), new string('a', 40), D(18, 10), D(18, 11))]).Lines[0].Label);
        var cut = Build([P(E("a"), new string('a', 41), D(18, 10), D(18, 11))]).Lines[0].Label;
        Assert.EndsWith("  " + new string('a', 39) + "…", cut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Blank_subject(string subject)
    {
        Assert.EndsWith("  (no subject)", Build([P(E("a"), subject, D(18, 10), D(18, 11))]).Lines[0].Label);
    }

    [Fact]
    public void Control_chars_cleaned()
    {
        Assert.EndsWith("  Budget review 2", Build([P(E("a"), "Budget\r\nreview\t 2", D(18, 10), D(18, 11))]).Lines[0].Label);
    }

    [Fact]
    public void Ampersand_escaped_in_text_only()
    {
        var l = Build([P(E("a"), "R&D sync", D(18, 10), D(18, 11))]).Lines[0];
        Assert.EndsWith("R&D sync", l.Label);
        Assert.EndsWith("R&&D sync", l.Text);
    }

    [Fact]
    public void Shared_suffix_display_name()
    {
        var m = Build([P(E("a", "help@x.edu"), "x", D(18, 10), D(18, 11))],
            shared: [new SharedMailbox { Smtp = "Help@X.edu", DisplayName = "Helpdesk" }]);
        Assert.EndsWith(" (Helpdesk)", m.Lines[0].Label);
    }

    [Fact]
    public void Shared_suffix_falls_back_to_smtp()
    {
        var p = new[] { P(E("a", "help@x.edu"), "x", D(18, 10), D(18, 11)) };
        Assert.EndsWith(" (help@x.edu)", Build(p, shared: [new SharedMailbox { Smtp = "help@x.edu", DisplayName = " " }]).Lines[0].Label);
        Assert.EndsWith(" (help@x.edu)", Build(p, shared: [new SharedMailbox { Smtp = "other@x.edu", DisplayName = "Other" }]).Lines[0].Label);
        Assert.EndsWith(" (help@x.edu)", Build(p, shared: [null, new SharedMailbox { Smtp = "other@x.edu" }]).Lines[0].Label);
    }

    [Fact]
    public void Shared_suffix_cut()
    {
        var m = Build([P(E("a", "help@x.edu"), "x", D(18, 10), D(18, 11))],
            shared: [new SharedMailbox { Smtp = "help@x.edu", DisplayName = new string('n', 50) }]);
        Assert.EndsWith(" (" + new string('n', 39) + "…)", m.Lines[0].Label);
    }

    [Fact]
    public void Own_calendar_no_suffix()
    {
        Assert.DoesNotContain(" (", Build([P(E("a"), "x", D(18, 10), D(18, 11))]).Lines[0].Label);
    }

    [Fact]
    public void No_details_line()
    {
        Assert.Equal("Draft invite saved Sat 11/14 10:00", Build([P(E("a"))]).Lines[0].Label);
        var m = Build([P(E("a", "help@x.edu"))], shared: [new SharedMailbox { Smtp = "help@x.edu", DisplayName = "Helpdesk" }]);
        Assert.Equal("Draft invite saved Sat 11/14 10:00 (Helpdesk)", m.Lines[0].Label);
    }

    [Fact]
    public void Max_15_lines_then_more()
    {
        List<PendingDraftInvite> Make(int n) => [.. Enumerable.Range(0, n).Select(i => P(E("e" + i), "x", D(18, 10).AddMinutes(i), D(18, 11)))];
        Assert.Equal(15, Build(Make(15)).Lines.Count);
        var m16 = Build(Make(16));
        Assert.Equal(16, m16.Lines.Count);
        Assert.Equal("and 1 more", m16.Lines[15].Label);
        Assert.Null(m16.Lines[15].Entry);
        var m40 = Build(Make(40));
        Assert.Equal(16, m40.Lines.Count);
        Assert.Equal("and 25 more", m40.Lines[15].Text);
        Assert.Equal("Draft invites (40)", m40.Header);
    }

    [Theory]
    [InlineData(OutlookConnectionState.Available, true, false)]
    [InlineData(OutlookConnectionState.Unavailable, false, true)]
    [InlineData(OutlookConnectionState.NotResponding, false, false)]
    [InlineData(OutlookConnectionState.Unknown, false, false)]
    public void State_table(OutlookConnectionState state, bool enabled, bool hint)
    {
        var m = Build([P(E("a"), "x", D(18, 10), D(18, 11))], state);
        Assert.Equal(enabled, m.LinesEnabled);
        Assert.Equal(hint, m.StartOutlookHint);
        Assert.Single(m.Lines);
    }

    [Fact]
    public void Texts()
    {
        Assert.Equal("Start Outlook to open them", DraftInviteMenu.StartOutlookHintText);
        Assert.Equal("That draft invite is no longer in your calendar.", DraftInviteMenu.GoneText);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DraftInviteMenu.Build(null!, OutlookConnectionState.Available, [], Tz));
        Assert.Throws<ArgumentNullException>(() => DraftInviteMenu.Build([], OutlookConnectionState.Available, null!, Tz));
        Assert.Throws<ArgumentNullException>(() => DraftInviteMenu.Build([], OutlookConnectionState.Available, [], null!));
        var m = Build([null!, P(E("a"), "x", D(18, 10), D(18, 11))]);
        Assert.Equal("Draft invites (1)", m.Header);
        Assert.Single(m.Lines);
    }

    [Fact]
    public void Clean_and_MenuText()
    {
        Assert.Equal("", DraftInviteMenu.Clean(null));
        Assert.Equal("a b c", DraftInviteMenu.Clean(" a\u0000b  c "));
        Assert.Equal("a&&&&b&&", DraftInviteMenu.MenuText("a&&b&"));
    }
}
