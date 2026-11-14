namespace ClippysRevenge.Core.Tests;

public class MeetingRankingTests
{
    private static DateTime D(int day, int h, int m = 0) => new(2025, 11, day, h, m, 0);
    private static TimeRange R(DateTime a, DateTime b) => new(a, b);

    // Fake free/busy: build an OwnerCalendar from a string anchored at Mon 11/17 00:00, 30-minute slots... simpler: 15-minute slots.
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

    private static readonly TimeRange BusyTue = R(D(18, 10), D(18, 11));

    private static MeetingTier Tier(TimeRange slot, OwnerCalendar o) => MeetingRanking.TierOf(slot, o);

    [Fact]
    public void AdjacentAfterAndBefore()
    {
        var o = Owner([BusyTue], []);
        Assert.Equal(MeetingTier.Busy, Tier(R(D(18, 11), D(18, 12)), o));
        Assert.Equal(MeetingTier.Busy, Tier(R(D(18, 9), D(18, 10)), o));
    }

    [Fact]
    public void Tolerance()
    {
        var o = Owner([BusyTue], []);
        Assert.Equal(MeetingTier.Busy, Tier(R(D(18, 11, 15), D(18, 12, 15)), o));
        Assert.Equal(MeetingTier.Busy, Tier(R(D(18, 8, 45), D(18, 9, 45)), o));
        Assert.Equal(MeetingTier.None, Tier(R(D(18, 11, 16), D(18, 12, 16)), o));
    }

    [Fact]
    public void NotAdjacentAcrossMidnight()
    {
        var o1 = Owner([R(D(17, 23, 30), D(18, 0))], []);
        Assert.Equal(MeetingTier.None, Tier(R(D(18, 0), D(18, 0, 30)), o1));
        var o2 = Owner([R(D(18, 0), D(18, 0, 30))], []);
        Assert.Equal(MeetingTier.None, Tier(R(D(17, 23, 30), D(18, 0)), o2));
    }

    [Fact]
    public void OverlapIsNone()
    {
        var o = Owner([BusyTue], []);
        Assert.Equal(MeetingTier.None, Tier(R(D(18, 10, 30), D(18, 11, 30)), o));
    }

    [Fact]
    public void BusyBeatsTentative()
    {
        var o = Owner([BusyTue], [R(D(18, 12), D(18, 13))]);
        Assert.Equal(MeetingTier.Busy, Tier(R(D(18, 11), D(18, 12)), o));
        Assert.Equal(MeetingTier.Tentative, Tier(R(D(18, 13), D(18, 14)), o));
    }

    [Fact]
    public void UnknownOwnerIsNone() =>
        Assert.Equal(MeetingTier.None, Tier(R(D(18, 11), D(18, 12)), OwnerCalendar.Unknown));

    [Fact]
    public void RankOrder()
    {
        var o = Owner([R(D(21, 10), D(21, 11))], [R(D(19, 12), D(19, 13))]);
        var ranked = MeetingRanking.Rank(
            [R(D(19, 9), D(19, 10)), R(D(21, 11), D(21, 12)), R(D(18, 13), D(18, 14)), R(D(19, 13), D(19, 14))], o);
        Assert.Equal(
            [R(D(21, 11), D(21, 12)), R(D(19, 13), D(19, 14)), R(D(18, 13), D(18, 14)), R(D(19, 9), D(19, 10))],
            ranked.Select(r => r.Range).ToArray());
        Assert.Equal([MeetingTier.Busy, MeetingTier.Tentative, MeetingTier.None, MeetingTier.None],
            ranked.Select(r => r.Tier).ToArray());
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("busy", MeetingRanking.Name(MeetingTier.Busy));
        Assert.Equal("tentative", MeetingRanking.Name(MeetingTier.Tentative));
        Assert.Equal("none", MeetingRanking.Name(MeetingTier.None));
    }
}
