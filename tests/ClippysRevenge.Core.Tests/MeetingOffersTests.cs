using System.Globalization;

namespace ClippysRevenge.Core.Tests;

public class MeetingOffersTests
{
    private static readonly TimeZoneInfo PT = TestZones.Pacific();
    private static readonly TimeZoneInfo UTC = TestZones.Utc();
    private static readonly TimeZoneInfo BER = TestZones.Berlin();

    private static DateTime D(int day, int h, int m = 0) => new(2025, 11, day, h, m, 0);
    private static TimeRange R(DateTime a, DateTime b) => new(a, b);
    private static TimeRange Blk(int day, int h1, int h2) => R(D(day, h1), D(day, h2));

    private static OwnerCalendar Owner(IEnumerable<TimeRange> busy, IEnumerable<TimeRange> tentative)
    {
        var origin = new DateTime(2025, 11, 17);
        var chars = new char[7 * 96];
        Array.Fill(chars, '0');
        void Mark(IEnumerable<TimeRange> rs, char c)
        {
            foreach (var r in rs)
            {
                var from = (int)((r.Start - origin).TotalMinutes / 15);
                var to = (int)((r.End - origin).TotalMinutes / 15);
                for (var i = from; i < to; i++) chars[i] = c;
            }
        }
        Mark(tentative, '1');
        Mark(busy, '2');
        return OwnerCalendar.FromFreeBusy(new string(chars), origin, 15);
    }

    private static MeetingOfferSet Build(IReadOnlyList<TimeRange> blocks, int count, TimeZoneInfo local,
        bool europe = false, TimeZoneInfo? cet = null, OwnerCalendar? owner = null, int dur = 60) =>
        MeetingOffers.Build(blocks, owner ?? OwnerCalendar.Unknown, dur, count, local, null, europe, cet);

    // ---------------------------------------------------------------- T3

    [Fact]
    public void CandidatesStepAndEndAligned()
    {
        var c = MeetingOffers.Candidates([R(D(18, 9), D(18, 11, 15))], 60);
        Assert.Equal(
            [R(D(18, 9), D(18, 10)), R(D(18, 9, 30), D(18, 10, 30)), R(D(18, 10), D(18, 11)), R(D(18, 10, 15), D(18, 11, 15))],
            c.ToArray());
    }

    [Fact]
    public void CandidatesEdgeCases()
    {
        Assert.Single(MeetingOffers.Candidates([Blk(18, 9, 10)], 60));
        Assert.Empty(MeetingOffers.Candidates([R(D(18, 9), D(18, 9, 45))], 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeetingOffers.Candidates([Blk(18, 9, 10)], 0));
    }

    [Fact]
    public void SelectionOrderByTier()
    {
        var owner = Owner([Blk(21, 10, 11)], [R(D(19, 10), D(19, 10, 30))]);
        var set = Build([Blk(18, 13, 14), Blk(19, 9, 10), Blk(21, 11, 12)], 3, PT, owner: owner);
        Assert.Equal(
            ["1. Fri 11/21  11:00–12:00 PT", "2. Wed 11/19  09:00–10:00 PT", "3. Tue 11/18  13:00–14:00 PT"],
            set.Main.Select(o => o.Line).ToArray());
        Assert.Equal([MeetingTier.Busy, MeetingTier.Tentative, MeetingTier.None], set.Main.Select(o => o.Tier).ToArray());
    }

    [Fact]
    public void NoOverlaps()
    {
        var set = Build([Blk(18, 9, 12)], 5, PT);
        Assert.Equal(3, set.Count);
        Assert.Equal([D(18, 9), D(18, 10), D(18, 11)], set.Main.Select(o => o.Range.Start).ToArray());
    }

    [Fact]
    public void CountLimits()
    {
        Assert.Equal(2, Build([Blk(18, 9, 17)], 2, PT).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => Build([Blk(18, 9, 17)], 0, PT));
    }

    [Fact]
    public void SelectHonoursAlreadyTaken()
    {
        var sel = MeetingOffers.Select([Blk(18, 9, 10), Blk(18, 10, 11)], OwnerCalendar.Unknown, 2,
            [R(D(18, 9, 30), D(18, 10, 30))]);
        Assert.Empty(sel);
    }

    [Fact]
    public void NoCandidatesIsEmpty()
    {
        var set = Build([R(D(18, 9), D(18, 9, 30))], 3, PT);
        Assert.Equal(0, set.Count);
        Assert.Equal("", set.ToText());
        Assert.False(set.CetShown);
        Assert.False(set.CetMissing);
        Assert.Empty(MeetingOfferSet.Empty.All);
    }

    // ---------------------------------------------------------------- T4

    [Fact]
    public void EuropeAllInHours()
    {
        var set = Build([Blk(18, 8, 9), Blk(19, 8, 9)], 2, PT, true, BER);
        Assert.Equal(
            "1. Tue 11/18  08:00–09:00 PT  (17:00–18:00 CET)\n2. Wed 11/19  08:00–09:00 PT  (17:00–18:00 CET)",
            set.ToText());
        Assert.Empty(set.OutsideHours);
        Assert.True(set.CetShown);
    }

    [Fact]
    public void EuropeMixed()
    {
        var set = Build([Blk(18, 8, 10), Blk(19, 13, 14)], 3, PT, true, BER);
        Assert.Equal(
            "1. Tue 11/18  08:00–09:00 PT  (17:00–18:00 CET)\n\nOutside European office hours:\n"
            + "2. Tue 11/18  09:00–10:00 PT  (18:00–19:00 CET)\n3. Wed 11/19  13:00–14:00 PT  (22:00–23:00 CET)",
            set.ToText());
        Assert.Equal([MeetingOfferList.Main, MeetingOfferList.OutsideHours, MeetingOfferList.OutsideHours],
            set.All.Select(o => o.List).ToArray());
        Assert.Equal(["main", "outside_hours", "outside_hours"], set.All.Select(o => o.ListName).ToArray());
        Assert.Equal([1, 2, 3], set.All.Select(o => o.N).ToArray());
    }

    [Fact]
    public void EuropeNoneInHours()
    {
        var set = Build([Blk(19, 13, 15)], 2, PT, true, BER);
        Assert.Equal(
            "Outside European office hours:\n1. Wed 11/19  13:00–14:00 PT  (22:00–23:00 CET)\n2. Wed 11/19  14:00–15:00 PT  (23:00–00:00 CET)",
            set.ToText());
        Assert.Empty(set.Main);
    }

    [Theory]
    [InlineData(7, 0, 8, 0, true)]
    [InlineData(16, 0, 17, 0, true)]
    [InlineData(16, 30, 17, 30, false)]
    [InlineData(6, 30, 7, 30, false)]
    public void EuropeBoundaries(int h1, int m1, int h2, int m2, bool inHours)
    {
        var set = Build([R(D(18, h1, m1), D(18, h2, m2))], 1, UTC, true, BER);
        var o = Assert.Single(set.All);
        Assert.Equal(inHours ? MeetingOfferList.Main : MeetingOfferList.OutsideHours, o.List);
    }

    [Fact]
    public void EuropeSummer()
    {
        var set = Build([R(new DateTime(2025, 7, 15, 6, 0, 0), new DateTime(2025, 7, 15, 7, 0, 0))], 1, UTC, true, BER);
        var o = Assert.Single(set.Main);
        Assert.Equal("1. Tue 7/15  06:00–07:00 UTC  (08:00–09:00 CET)", o.Line);
    }

    [Fact]
    public void OwnerAlreadyInCet()
    {
        var set = Build([Blk(18, 19, 20)], 1, BER, true, BER);
        Assert.False(set.CetShown);
        Assert.DoesNotContain("(", set.Main[0].Line);
        Assert.Empty(set.OutsideHours);
        Assert.DoesNotContain("Outside", set.ToText());
    }

    [Fact]
    public void CetZoneMissing()
    {
        var set = Build([Blk(18, 8, 10), Blk(19, 13, 14)], 3, PT, true, null);
        Assert.True(set.CetMissing);
        Assert.False(set.CetShown);
        Assert.Equal([D(18, 8), D(18, 9), D(19, 13)], set.Main.Select(o => o.Range.Start).ToArray());
        Assert.All(set.All, o => Assert.Null(o.Cet));
        Assert.Empty(set.OutsideHours);
    }

    [Fact]
    public void EuropeFalseHasNoCet()
    {
        var set = Build([Blk(19, 13, 15)], 2, PT, false, BER);
        Assert.False(set.CetMissing);
        Assert.False(set.CetShown);
        Assert.All(set.All, o => Assert.DoesNotContain("CET", o.Line));
        Assert.Empty(set.OutsideHours);
    }

    [Fact]
    public void ModelHeadingInText()
    {
        var set = Build([Blk(18, 8, 10), Blk(19, 13, 14)], 3, PT, true, BER);
        Assert.Contains("\nAußerhalb der Bürozeiten in Europa:\n", set.ToText("Außerhalb der Bürozeiten in Europa"));
    }

    // ---------------------------------------------------------------- T5

    [Fact]
    public void ExampleLines()
    {
        var owner = Owner([Blk(18, 9, 10), Blk(19, 13, 14)], []);
        var set = Build([Blk(18, 10, 11), Blk(19, 14, 15)], 2, PT, true, BER, owner);
        Assert.Empty(set.Main);
        Assert.Equal("1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)", set.OutsideHours[0].Line);
        Assert.Equal("2. Wed 11/19  14:00–15:00 PT  (23:00–00:00 CET)", set.OutsideHours[1].Line);
        Assert.All(set.OutsideHours, o => Assert.Equal(MeetingTier.Busy, o.Tier));
        Assert.Equal("1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)",
            MeetingOffers.FormatLine(1, Blk(18, 10, 11), "PT", Blk(18, 19, 20)));
        Assert.Equal("2. Wed 11/19  14:00–15:00 PT  (23:00–00:00 CET)",
            MeetingOffers.FormatLine(2, Blk(19, 14, 15), "PT", R(D(19, 23), D(20, 0))));
    }

    [Fact]
    public void FormatLineMidnightEnd() =>
        Assert.Equal("1. Mon 11/17  23:00–00:00 PT", MeetingOffers.FormatLine(1, R(D(17, 23), D(18, 0)), "PT", null));

    [Fact]
    public void FormatLineBlankLabel()
    {
        Assert.Equal("3. Tue 11/18  10:00–11:00", MeetingOffers.FormatLine(3, Blk(18, 10, 11), "  ", null));
        Assert.Equal("3. Tue 11/18  10:00–11:00", MeetingOffers.FormatLine(3, Blk(18, 10, 11), null, null));
    }

    [Fact]
    public void FormatLineUsesEnDashAndInvariantCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var line = MeetingOffers.FormatLine(1, Blk(18, 10, 11), "PT", Blk(18, 19, 20));
            Assert.Equal("1. Tue 11/18  10:00–11:00 PT  (19:00–20:00 CET)", line);
            Assert.Contains('–', line);
            Assert.DoesNotContain("10:00-11:00", line);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void FormatLineValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MeetingOffers.FormatLine(0, Blk(18, 10, 11), "PT", null));
        Assert.Throws<ArgumentException>(() => MeetingOffers.FormatLine(1, R(D(18, 11), D(18, 10)), "PT", null));
        Assert.Throws<ArgumentException>(() => MeetingOffers.FormatLine(1, Blk(18, 10, 11), "PT", R(D(18, 20), D(18, 19))));
    }

    // ---------------------------------------------------------------- T6

    [Theory]
    [InlineData(null, "Outside European office hours:")]
    [InlineData("", "Outside European office hours:")]
    [InlineData("   ", "Outside European office hours:")]
    [InlineData("Außerhalb der Bürozeiten", "Außerhalb der Bürozeiten:")]
    [InlineData("Hors des heures de bureau européennes :", "Hors des heures de bureau européennes :")]
    [InlineData("a\u0007b", "ab:")]
    [InlineData("x \t\n  y", "x y:")]
    [InlineData("[[x]] <b>", "x b:")]
    [InlineData("12345", "Outside European office hours:")]
    public void Heading(string? input, string expected) => Assert.Equal(expected, MeetingOffers.Heading(input));

    [Fact]
    public void HeadingLength()
    {
        Assert.Equal(new string('a', 80) + ":", MeetingOffers.Heading(new string('a', 80)));
        Assert.Equal(MeetingOffers.OutsideHoursHeading, MeetingOffers.Heading(new string('a', 81)));
    }
}
