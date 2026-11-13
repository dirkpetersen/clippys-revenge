using System.Globalization;

namespace ClippysRevenge.Core.Tests;

public class SlotFormatterTests
{
    private static DateTime D(int d, int h, int m = 0, int s = 0) => new(2024, 10, d, h, m, s);

    [Fact]
    public void Basic() =>
        Assert.Equal("1. Tue 10/15 1-3 PT", SlotFormatter.Format(1, D(15, 13), D(15, 15), "PT", 8, 17));

    [Fact]
    public void Minutes() =>
        Assert.Equal("2. Wed 10/16 9:30-11 PT", SlotFormatter.Format(2, D(16, 9, 30), D(16, 11), "PT", 8, 17));

    [Fact]
    public void EndMinutes() =>
        Assert.Equal("3. Thu 10/17 11-1:15 ET", SlotFormatter.Format(3, D(17, 11), D(17, 13, 15), "ET", 8, 17));

    [Fact]
    public void WideStart() =>
        Assert.Equal("1. Tue 10/15 6-8am PT", SlotFormatter.Format(1, D(15, 6), D(15, 8), "PT", 6, 17));

    [Fact]
    public void WideEnd() =>
        Assert.Equal("1. Tue 10/15 11am-1pm PT", SlotFormatter.Format(1, D(15, 11), D(15, 13), "PT", 8, 20));

    [Fact]
    public void WideEndSameSuffix() =>
        Assert.Equal("1. Tue 10/15 1:30-3pm PT", SlotFormatter.Format(1, D(15, 13, 30), D(15, 15), "PT", 8, 20));

    [Fact]
    public void Exactly7To19NoAmPm() =>
        Assert.Equal("1. Tue 10/15 7-9 PT", SlotFormatter.Format(1, D(15, 7), D(15, 9), "PT", 7, 19));

    [Fact]
    public void MidnightEnd() =>
        Assert.Equal("1. Tue 10/15 11pm-12am PT",
            SlotFormatter.Format(1, D(15, 23), new DateTime(2024, 10, 16, 0, 0, 0), "PT", 0, 24));

    [Fact]
    public void Noon() =>
        Assert.Equal("1. Tue 10/15 12-12:30 PT", SlotFormatter.Format(1, D(15, 12), D(15, 12, 30), "PT", 8, 17));

    [Fact]
    public void NoLeadingZeros() =>
        Assert.Equal("12. Fri 1/5 9-10 PT",
            SlotFormatter.Format(12, new DateTime(2024, 1, 5, 9, 0, 0), new DateTime(2024, 1, 5, 10, 0, 0), "PT", 8, 17));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankLabel(string? label) =>
        Assert.Equal("1. Tue 10/15 1-3", SlotFormatter.Format(1, D(15, 13), D(15, 15), label, 8, 17));

    [Fact]
    public void LabelTrimmed() =>
        Assert.Equal("1. Tue 10/15 1-3 PT", SlotFormatter.Format(1, D(15, 13), D(15, 15), " PT ", 8, 17));

    [Fact]
    public void SecondsIgnored() =>
        Assert.Equal("1. Tue 10/15 1-3 PT", SlotFormatter.Format(1, D(15, 13, 0, 59), D(15, 15, 0, 30), "PT", 8, 17));

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void KindIgnored(DateTimeKind kind) =>
        Assert.Equal("1. Tue 10/15 1-3 PT",
            SlotFormatter.Format(1, DateTime.SpecifyKind(D(15, 13), kind), DateTime.SpecifyKind(D(15, 15), kind), "PT", 8, 17));

    [Fact]
    public void InvariantCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1. Tue 10/15 1-3 PT", SlotFormatter.Format(1, D(15, 13), D(15, 15), "PT", 8, 17));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void BadN()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SlotFormatter.Format(0, D(15, 13), D(15, 15), "PT", 8, 17));
        Assert.Equal("n", ex.ParamName);
    }

    [Theory]
    [InlineData(17, 8)]
    [InlineData(8, 8)]
    [InlineData(-1, 17)]
    [InlineData(8, 25)]
    public void BadWindow(int s, int e) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SlotFormatter.Format(1, D(15, 13), D(15, 15), "PT", s, e));

    [Theory]
    [InlineData(15, 13)]
    [InlineData(15, 12)]
    public void EndNotAfterStart(int endDay, int endHour)
    {
        var ex = Assert.Throws<ArgumentException>(() => SlotFormatter.Format(1, D(15, 13), D(endDay, endHour), "PT", 8, 17));
        Assert.Equal("end", ex.ParamName);
        Assert.StartsWith("Slot end must be after its start.", ex.Message);
    }

    [Fact]
    public void DifferentDay()
    {
        var ex = Assert.Throws<ArgumentException>(() => SlotFormatter.Format(1, D(15, 22), D(16, 1), "PT", 0, 24));
        Assert.StartsWith("Slot must start and end on the same day.", ex.Message);
    }

    [Theory]
    [InlineData(8, 17, false)]
    [InlineData(7, 19, false)]
    [InlineData(6, 17, true)]
    [InlineData(8, 20, true)]
    [InlineData(0, 24, true)]
    public void NeedsAmPm(int s, int e, bool expected) =>
        Assert.Equal(expected, SlotFormatter.NeedsAmPm(s, e));
}
