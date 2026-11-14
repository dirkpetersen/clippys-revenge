using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// Not live: runs in CI on Windows. The pure helpers of the live meeting-times tests (M13.7): the SPEC 21.5 line
/// parser, the HTML line reader and the free/busy guard.
/// </summary>
[Trait("os", "windows")]
public sealed class LiveMeetingChecksTests
{
    private static OfferLine P(string line) => LiveMeetingChecks.Parse(line) ?? throw new InvalidOperationException("no parse");

    // ---- Parse

    [Fact]
    public void Parse_reads_a_line_with_label_and_cet()
    {
        var p = P("1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)");
        Assert.Equal(new OfferLine(1, "Tue", 11, 18, new TimeOnly(10, 0), new TimeOnly(11, 0), "PT",
            new TimeOnly(19, 0), new TimeOnly(20, 0)), p);
    }

    [Fact]
    public void Parse_accepts_a_midnight_cet_end()
    {
        var p = P("2. Wed 11/19  14:00–15:00 PT  (23:00–00:00 CET)");
        Assert.Equal(new TimeOnly(0, 0), p.CetEnd);
    }

    [Fact]
    public void Parse_reads_a_line_without_label_and_cet()
    {
        var p = P("3. Mon 11/16  09:00–10:00");
        Assert.Null(p.Label);
        Assert.Null(p.CetStart);
        Assert.Null(p.CetEnd);
    }

    [Fact]
    public void Parse_reads_a_two_digit_number_and_an_offset_label()
    {
        var p = P("10. Mon 11/16  09:00–10:00 UTC+05:30");
        Assert.Equal(10, p.N);
        Assert.Equal("UTC+05:30", p.Label);
    }

    [Theory]
    [InlineData("1. Mon 11/16  09:00-10:00 PT")]
    [InlineData("1. Mon 11/16 09:00–10:00 PT")]
    [InlineData("0. Mon 11/16  09:00–10:00 PT")]
    [InlineData("1. Mon 13/16  09:00–10:00 PT")]
    [InlineData("1. Monday 11/16  09:00–10:00 PT")]
    [InlineData("1. Mon 11/16  9:00–10:00 PT")]
    [InlineData("1. Mon 11/16  09:00–10:00 PT  (19:00–20:00 CEST)")]
    [InlineData("1. Mon 11/16  09:00–10:00 PT (19:00–20:00 CET)")]
    [InlineData(" 1. Mon 11/16  09:00–10:00 PT")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_rejects_other_shapes(string? line) => Assert.Null(LiveMeetingChecks.Parse(line));

    private static MeetingOfferSet Set() =>
        MeetingOffers.Build([new TimeRange(new DateTime(2026, 11, 16, 8, 0, 0), new DateTime(2026, 11, 16, 12, 0, 0))],
            OwnerCalendar.Unknown, 60, 3, TestZones.Pacific(), "PT", europe: true, TestZones.Berlin());

    [Fact]
    public void Parse_reads_every_line_the_product_builds()
    {
        var set = Set();
        Assert.NotEmpty(set.OutsideHours);
        foreach (var offer in set.All)
        {
            var p = P(offer.Line);
            Assert.Equal(offer.N, p.N);
            Assert.Equal(TimeOnly.FromDateTime(offer.Cet!.Value.Start), p.CetStart);
        }
    }

    // ---- HtmlLines, Collapse, Minutes

    [Fact]
    public void HtmlLines_gives_the_inserted_block_line_by_line()
    {
        var set = Set();
        var html = MeetingTimesHtml.Insert("<p>Times:</p><p>[[meeting_times]]</p><p>Best</p>", set);

        var lines = LiveMeetingChecks.HtmlLines(html);

        var expected = new List<string> { "Times:" };
        expected.AddRange(set.Main.Select(o => o.Line));
        expected.Add(LiveMeetingChecks.DefaultHeading);
        expected.AddRange(set.OutsideHours.Select(o => o.Line));
        expected.Add("Best");
        Assert.Equal(expected, lines);
        Assert.All(set.All, o => Assert.NotNull(LiveMeetingChecks.Parse(o.Line)));
    }

    [Fact]
    public void HtmlLines_keeps_spaces_decodes_entities_and_drops_tags()
    {
        Assert.Equal(["a&b", "c  de"],
            LiveMeetingChecks.HtmlLines("<P>a&amp;b</P><BR/>c&nbsp; d<span style=\"x\">e</span>"));
        Assert.Empty(LiveMeetingChecks.HtmlLines(null));
    }

    [Fact]
    public void Collapse_squeezes_spaces_and_trims_each_line()
    {
        Assert.Equal("1. Mon 11/16 09:00–10:00 PT\n2. x",
            LiveMeetingChecks.Collapse("1. Mon 11/16  09:00–10:00 PT\r\n 2.  x "));
        Assert.Equal("", LiveMeetingChecks.Collapse(null));
    }

    [Fact]
    public void Minutes_handles_midnight_ends()
    {
        Assert.Equal(60, LiveMeetingChecks.Minutes(new TimeOnly(10, 0), new TimeOnly(11, 0)));
        Assert.Equal(30, LiveMeetingChecks.Minutes(new TimeOnly(23, 30), new TimeOnly(0, 0)));
        Assert.Equal(30, LiveMeetingChecks.Minutes(new TimeOnly(9, 30), new TimeOnly(10, 0)));
    }

    // ---- GuardedFreeBusySource

    private static FakeFreeBusySource Inner()
    {
        var f = new FakeFreeBusySource();
        f.Free("me@x.org");
        return f;
    }

    private static readonly DateTime From = new(2026, 11, 16);

    [Fact]
    public async Task Guard_passes_the_allowed_address_after_the_model_returned()
    {
        var inner = Inner();
        var g = new GuardedFreeBusySource(inner, "me@x.org", () => true);

        var fb = await g.GetFreeBusyAsync(" ME@X.org ", From, From.AddDays(1), 30, WorkPriority.Interactive,
            TestContext.Current.CancellationToken);

        Assert.NotNull(fb);
        Assert.Equal(1, g.Calls);
        Assert.Equal([WorkPriority.Interactive], g.Priorities);
        Assert.Null(g.Tripped);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Guard_refuses_another_address_without_reaching_the_inner_source()
    {
        var inner = Inner();
        var g = new GuardedFreeBusySource(inner, "me@x.org", () => true);

        var fb = await g.GetFreeBusyAsync("ann@y.org", From, From.AddDays(1), 30, WorkPriority.Interactive,
            TestContext.Current.CancellationToken);

        Assert.Null(fb);
        Assert.Equal("address", g.Tripped);
        Assert.Empty(inner.Calls);
        Assert.Equal(0, g.Calls);
    }

    [Fact]
    public async Task Guard_refuses_a_read_before_the_model_returned()
    {
        var inner = Inner();
        var g = new GuardedFreeBusySource(inner, "me@x.org", () => false);

        var fb = await g.GetFreeBusyAsync("me@x.org", From, From.AddDays(1), 30, WorkPriority.Interactive,
            TestContext.Current.CancellationToken);

        Assert.Null(fb);
        Assert.Equal("before-model", g.Tripped);
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public async Task Guard_refuses_the_current_user_lookup()
    {
        var inner = Inner();
        var g = new GuardedFreeBusySource(inner, "me@x.org", () => true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            g.GetCurrentUserAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken));

        Assert.Equal("current-user", g.Tripped);
        Assert.Equal(0, inner.CurrentUserCalls);
    }

    [Fact]
    public async Task Guard_keeps_the_first_reason()
    {
        var returned = true;
        var g = new GuardedFreeBusySource(Inner(), "me@x.org", () => returned);
        var ct = TestContext.Current.CancellationToken;

        await g.GetFreeBusyAsync("ann@y.org", From, From.AddDays(1), 30, WorkPriority.Interactive, ct);
        returned = false;
        await g.GetFreeBusyAsync("me@x.org", From, From.AddDays(1), 30, WorkPriority.Interactive, ct);

        Assert.Equal("address", g.Tripped);
    }
}
