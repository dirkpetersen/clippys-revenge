using System.Globalization;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class CalendarEventsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_are_today_midnight_plus_seven_days()
    {
        var w = CalendarEvents.ResolveWindow(null, null, 7, Utc, Now);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero), w.Start);
        Assert.Equal(w.Start.AddDays(7), w.End);
    }

    [Fact]
    public void Days_applies_to_given_start()
    {
        var w = CalendarEvents.ResolveWindow("2026-03-10", null, 3, Utc, Now);
        Assert.Equal(new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.Zero), w.End);
    }

    [Fact]
    public void Date_only_end_covers_whole_day()
    {
        var w = CalendarEvents.ResolveWindow("2026-03-10", "2026-03-10", 7, Utc, Now);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 23, 59, 59, TimeSpan.Zero), w.End);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(367)]
    public void Days_out_of_range_throws(int days) =>
        Assert.Throws<McpException>(() => CalendarEvents.ResolveWindow(null, null, days, Utc, Now));

    [Fact]
    public void End_before_start_throws() =>
        Assert.Throws<McpException>(() => CalendarEvents.ResolveWindow("2026-03-10T10:00", "2026-03-10T09:00", 7, Utc, Now));

    [Fact]
    public void Bad_date_names_the_argument()
    {
        var ex = Assert.Throws<McpException>(() => CalendarEvents.ResolveWindow("junk", null, 7, Utc, Now));
        Assert.Contains("start", ex.Message);
        ex = Assert.Throws<McpException>(() => CalendarEvents.ResolveWindow(null, "junk", 7, Utc, Now));
        Assert.Contains("end", ex.Message);
    }

    [Fact]
    public void Filter_is_culture_independent()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var w = new EventWindow(new DateTimeOffset(2026, 3, 4, 9, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 11, 17, 0, 0, TimeSpan.Zero));
            Assert.Equal(
                "@SQL=\"urn:schemas:calendar:dtend\" > '2026-03-04 09:05' AND \"urn:schemas:calendar:dtstart\" < '2026-03-11 17:00'",
                CalendarEvents.BuildFilter(w));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void DaslDate_is_utc()
    {
        Assert.Equal("2026-03-04 15:00", CalendarEvents.DaslDate(new DateTimeOffset(2026, 3, 4, 15, 0, 0, TimeSpan.Zero)));
        Assert.Equal("2026-03-04 13:00", CalendarEvents.DaslDate(new DateTimeOffset(2026, 3, 4, 5, 0, 0, TimeSpan.FromHours(-8))));
    }

    [Fact]
    public void BuildFilter_uses_utc_for_non_utc_window()
    {
        var o = TimeSpan.FromHours(-8);
        var w = new EventWindow(new DateTimeOffset(2026, 3, 4, 3, 0, 0, o), new DateTimeOffset(2026, 3, 4, 7, 0, 0, o));
        Assert.Equal(
            "@SQL=\"urn:schemas:calendar:dtend\" > '2026-03-04 11:00' AND \"urn:schemas:calendar:dtstart\" < '2026-03-04 15:00'",
            CalendarEvents.BuildFilter(w));
    }

    [Fact]
    public void FromLocal_attaches_offset()
    {
        var tz = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(2), "t", "t");
        var v = CalendarEvents.FromLocal(new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc), tz);
        Assert.Equal(TimeSpan.FromHours(2), v.Offset);
        Assert.Equal(9, v.Hour);
    }

    [Theory]
    [InlineData(0, "free")]
    [InlineData(1, "tentative")]
    [InlineData(2, "busy")]
    [InlineData(3, "out_of_office")]
    [InlineData(4, "working_elsewhere")]
    [InlineData(99, "busy")]
    public void BusyStatusName_maps(int v, string name) => Assert.Equal(name, CalendarEvents.BusyStatusName(v));
}
