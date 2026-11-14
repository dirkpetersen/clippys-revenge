namespace ClippysRevenge.Core.Tests;

/// <summary>Fixed fake zones so tests don't depend on the machine's time zone data.</summary>
internal static class TestZones
{
    public static TimeZoneInfo Pacific() =>
        TimeZoneInfo.CreateCustomTimeZone("Pacific Standard Time", TimeSpan.FromHours(-8), "Pacific", "Pacific Standard Time");

    public static TimeZoneInfo Utc() =>
        TimeZoneInfo.CreateCustomTimeZone("UTC", TimeSpan.Zero, "UTC", "UTC");

    public static TimeZoneInfo Berlin()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test/Berlin", TimeSpan.FromHours(1), "Berlin",
            "W. Europe Standard Time", "W. Europe Daylight Time", [rule]);
    }
}
