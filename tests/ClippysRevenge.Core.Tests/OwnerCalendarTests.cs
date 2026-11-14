namespace ClippysRevenge.Core.Tests;

public class OwnerCalendarTests
{
    private static DateTime D(int h, int m = 0) => new(2025, 11, 18, h, m, 0);
    private static TimeRange R(DateTime a, DateTime b) => new(a, b);

    [Fact]
    public void BusyAndTentativeRuns()
    {
        var c = OwnerCalendar.FromFreeBusy("0221100", D(8), 30);
        Assert.True(c.IsKnown);
        Assert.Equal([R(D(8, 30), D(9, 30))], c.Busy);
        Assert.Equal([R(D(9, 30), D(10, 30))], c.Tentative);
    }

    [Fact]
    public void ThreeSplitsRunAndIsNotBusy()
    {
        var c = OwnerCalendar.FromFreeBusy("2232", D(8), 30);
        Assert.Equal([R(D(8), D(9)), R(D(9, 30), D(10))], c.Busy);
        var n = OwnerCalendar.FromFreeBusy("0345", D(8), 30);
        Assert.True(n.IsKnown);
        Assert.Empty(n.Busy);
        Assert.Empty(n.Tentative);
    }

    [Theory]
    [InlineData("02x0")]
    [InlineData(null)]
    public void UnknownInput(string? fb)
    {
        var c = OwnerCalendar.FromFreeBusy(fb, D(8), 30);
        Assert.False(c.IsKnown);
        Assert.Empty(c.Busy);
        Assert.Empty(c.Tentative);
    }

    [Fact]
    public void EmptyIsKnown()
    {
        var c = OwnerCalendar.FromFreeBusy("", D(8), 30);
        Assert.True(c.IsKnown);
        Assert.Empty(c.Busy);
    }

    [Fact]
    public void RunAtEndIsClosed()
    {
        var c = OwnerCalendar.FromFreeBusy("0022", D(8), 30);
        Assert.Equal([R(D(9), D(10))], c.Busy);
    }

    [Fact]
    public void ZeroIntervalThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerCalendar.FromFreeBusy("0", D(8), 0));
}
