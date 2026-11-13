using Xunit;

namespace ClippysRevenge.Core.Tests;

public class SearchDatesTests
{
    private static readonly TimeZoneInfo Tz8 =
        TimeZoneInfo.CreateCustomTimeZone("Test-8", TimeSpan.FromHours(-8), "Test-8", "Test-8");

    private static DateTimeOffset D(string s) => DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static TimeZoneInfo La => TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private static void AssertSame(DateTimeOffset expected, DateTimeOffset? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value);
        Assert.Equal(expected.Offset, actual.Value.Offset);
    }

    [Fact]
    public void ParseFrom_DateOnly() => AssertSame(D("2026-03-01T00:00:00-08:00"), SearchDates.ParseFrom("2026-03-01", Tz8));

    [Fact]
    public void ParseTo_DateOnly_WholeDay() => AssertSame(D("2026-03-01T23:59:59-08:00"), SearchDates.ParseTo("2026-03-01", Tz8));

    [Fact]
    public void Parse_DateTime_Minutes()
    {
        AssertSame(D("2026-03-01T14:30:00-08:00"), SearchDates.ParseFrom("2026-03-01T14:30", Tz8));
        AssertSame(D("2026-03-01T14:30:00-08:00"), SearchDates.ParseTo("2026-03-01T14:30", Tz8));
    }

    [Fact]
    public void Parse_DateTime_SpaceSeparator() =>
        AssertSame(D("2026-03-01T14:30:15-08:00"), SearchDates.ParseFrom("2026-03-01 14:30:15", Tz8));

    [Fact]
    public void Parse_Fraction() =>
        AssertSame(D("2026-03-01T14:30:15.5-08:00"), SearchDates.ParseFrom("2026-03-01T14:30:15.5", Tz8));

    [Fact]
    public void Parse_Utc_Z()
    {
        AssertSame(D("2026-03-01T14:30:00+00:00"), SearchDates.ParseFrom("2026-03-01T14:30Z", Tz8));
        AssertSame(D("2026-03-01T14:30:00+00:00"), SearchDates.ParseTo("2026-03-01T14:30Z", Tz8));
    }

    [Fact]
    public void Parse_ExplicitOffset() =>
        AssertSame(D("2026-03-01T14:30:00+02:00"), SearchDates.ParseFrom("2026-03-01T14:30+02:00", Tz8));

    [Fact]
    public void Parse_Trims() => AssertSame(D("2026-03-01T00:00:00-08:00"), SearchDates.ParseFrom("  2026-03-01  ", Tz8));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrBlank(string? s)
    {
        Assert.Null(SearchDates.ParseFrom(s, Tz8));
        Assert.Null(SearchDates.ParseTo(s, Tz8));
    }

    [Theory]
    [InlineData("03/01/2026")]
    [InlineData("2026-3-1")]
    [InlineData("yesterday")]
    [InlineData("2026-02-30")]
    [InlineData("2026-03-01T25:00")]
    public void Parse_Invalid_Throws(string s)
    {
        Assert.Throws<FormatException>(() => SearchDates.ParseFrom(s, Tz8));
        var ex = Assert.Throws<FormatException>(() => SearchDates.ParseTo(s, Tz8));
        Assert.StartsWith("date_to \"", ex.Message);
    }

    [Fact]
    public void Parse_Invalid_Message()
    {
        var ex = Assert.Throws<FormatException>(() => SearchDates.ParseFrom(" yesterday ", Tz8));
        Assert.Equal(
            "date_from \"yesterday\" is not a valid date. Use an ISO date like 2026-03-01 or a date-time like 2026-03-01T14:30 (local time).",
            ex.Message);
    }

    [Fact]
    public void ParseTo_DstSpringForward() =>
        AssertSame(D("2026-03-08T23:59:59-07:00"), SearchDates.ParseTo("2026-03-08", La));

    [Fact]
    public void ParseFrom_DstFallBackDay()
    {
        AssertSame(D("2026-11-01T00:00:00-07:00"), SearchDates.ParseFrom("2026-11-01", La));
        AssertSame(D("2026-11-01T23:59:59-08:00"), SearchDates.ParseTo("2026-11-01", La));
    }

    [Fact]
    public void Parse_DstGap_NoThrow()
    {
        var r = SearchDates.ParseFrom("2026-03-08T02:30", La);
        Assert.Equal(TimeSpan.FromHours(-8), r!.Value.Offset);
    }

    [Fact]
    public void Parse_NullTz_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SearchDates.ParseFrom("2026-03-01", null!));
        Assert.Throws<ArgumentNullException>(() => SearchDates.ParseTo("2026-03-01", null!));
    }
}
