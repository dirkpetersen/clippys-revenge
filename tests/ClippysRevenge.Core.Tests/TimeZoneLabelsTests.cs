namespace ClippysRevenge.Core.Tests;

public class TimeZoneLabelsTests
{
    private static TimeZoneInfo Tz(string id, double hours, string? std = null) =>
        TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), id, std ?? id);

    [Theory]
    [InlineData("Pacific Standard Time", "PT")]
    [InlineData("Mountain Standard Time", "MT")]
    [InlineData("US Mountain Standard Time", "MT")]
    [InlineData("Central Standard Time", "CT")]
    [InlineData("Eastern Standard Time", "ET")]
    [InlineData("Alaskan Standard Time", "AKT")]
    [InlineData("Hawaiian Standard Time", "HT")]
    [InlineData("America/Los_Angeles", "PT")]
    [InlineData("America/Denver", "MT")]
    [InlineData("America/Phoenix", "MT")]
    [InlineData("America/Chicago", "CT")]
    [InlineData("America/New_York", "ET")]
    [InlineData("America/Indiana/Indianapolis", "ET")]
    [InlineData("America/Anchorage", "AKT")]
    [InlineData("Pacific/Honolulu", "HT")]
    [InlineData("pacific standard time", "PT")]
    public void UsZones(string id, string expected) =>
        Assert.Equal(expected, TimeZoneLabels.For(Tz(id, -8)));

    [Fact]
    public void MexicoFallsThrough() =>
        Assert.Equal("UTC-06:00", TimeZoneLabels.For(Tz("Central Standard Time (Mexico)", -6)));

    [Fact]
    public void NonUs() =>
        Assert.Equal("UTC+01:00", TimeZoneLabels.For(Tz("W. Europe Standard Time", 1)));

    private static TimeZoneInfo Berlin()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test/Berlin", TimeSpan.FromHours(1), "Berlin",
            "W. Europe Standard Time", "W. Europe Daylight Time", [rule]);
    }

    [Fact]
    public void OffsetAtDate() =>
        Assert.Equal("UTC+02:00", TimeZoneLabels.For(Berlin(), new DateTime(2024, 7, 1, 12, 0, 0)));

    [Fact]
    public void OffsetBaseWhenNoAt() =>
        Assert.Equal("UTC+01:00", TimeZoneLabels.For(Berlin()));

    [Fact]
    public void Abbreviation() =>
        Assert.Equal("JST", TimeZoneLabels.For(Tz("Test/Tokyo", 9, "JST")));

    [Theory]
    [InlineData("Jst")]
    [InlineData("GMT+09:00")]
    public void NotAbbreviation(string std) =>
        Assert.Equal("UTC+09:00", TimeZoneLabels.For(Tz("Test/X", 9, std)));

    [Theory]
    [InlineData("Test/India", 5.5, "India Standard Time", "UTC+05:30")]
    [InlineData("Test/Nepal", 5.75, "Nepal Standard Time", "UTC+05:45")]
    [InlineData("Test/Newfoundland", -3.5, "Newfoundland Standard Time", "UTC-03:30")]
    public void FractionalOffsets(string id, double h, string std, string expected) =>
        Assert.Equal(expected, TimeZoneLabels.For(Tz(id, h, std)));

    [Fact]
    public void SystemUtc() => Assert.Equal("UTC", TimeZoneLabels.For(TimeZoneInfo.Utc));

    [Fact]
    public void EtcUtc() =>
        Assert.Equal("UTC", TimeZoneLabels.For(Tz("Etc/UTC", 0, "Coordinated Universal Time")));

    [Fact]
    public void London() =>
        Assert.Equal("UTC+00:00", TimeZoneLabels.For(Tz("GMT Standard Time", 0)));

    [Fact]
    public void ForNull() => Assert.Throws<ArgumentNullException>(() => TimeZoneLabels.For(null!));

    [Fact]
    public void OverrideWins() =>
        Assert.Equal("PST", TimeZoneLabels.Resolve("PST", Tz("Pacific Standard Time", -8)));

    [Fact]
    public void OverrideTrimmed() =>
        Assert.Equal("Berlin", TimeZoneLabels.Resolve("  Berlin  ", Tz("W. Europe Standard Time", 1)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoOverride(string? o) =>
        Assert.Equal("PT", TimeZoneLabels.Resolve(o, Tz("Pacific Standard Time", -8)));

    [Fact]
    public void ResolveNull() => Assert.Throws<ArgumentNullException>(() => TimeZoneLabels.Resolve("PT", null!));

    [Theory]
    [Trait("os", "windows")]
    [InlineData("Pacific Standard Time", "PT")]
    [InlineData("Eastern Standard Time", "ET")]
    [InlineData("Tokyo Standard Time", "UTC+09:00")]
    public void WindowsSystemZones(string id, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.Equal(expected, TimeZoneLabels.For(TimeZoneInfo.FindSystemTimeZoneById(id)));
    }
}
