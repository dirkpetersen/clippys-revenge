using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class SearchArgsTests
{
    [Fact]
    public void A1_Default()
    {
        var f = new SearchArgs().ToFilter(TimeZoneInfo.Utc);
        Assert.Equal(50, f.Limit);
        Assert.Equal(0, f.Offset);
        Assert.Null(f.Query);
        Assert.Null(f.DateFrom);
        Assert.Null(f.HasAttachment);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(200, 200)]
    [InlineData(201, 200)]
    public void A2_LimitClamped(int given, int expected) =>
        Assert.Equal(expected, new SearchArgs { Limit = given }.ToFilter(TimeZoneInfo.Utc).Limit);

    [Fact]
    public void A3_NegativeOffset() => Assert.Equal(0, new SearchArgs { Offset = -3 }.ToFilter(TimeZoneInfo.Utc).Offset);

    [Fact]
    public void A4_Dates()
    {
        var f = new SearchArgs { DateFrom = "2026-03-01", DateTo = "2026-03-01" }.ToFilter(TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), f.DateFrom);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 23, 59, 59, TimeSpan.Zero), f.DateTo);
    }

    [Fact]
    public void A5_BadDate()
    {
        var ex = Assert.Throws<McpException>(() => new SearchArgs { DateTo = "nope" }.ToFilter(TimeZoneInfo.Utc));
        Assert.StartsWith("date_to \"nope\" is not a valid date.", ex.Message);
    }

    [Fact]
    public void A6_Copied()
    {
        var f = new SearchArgs
        {
            Query = "q", Sender = "s", Recipient = "r", Subject = "su", Folder = "f", HasAttachment = true,
            AttachmentName = "n", AttachmentExt = "e", Unread = false,
        }.ToFilter(TimeZoneInfo.Utc);
        Assert.Equal(("q", "s", "r", "su", "f"), (f.Query, f.Sender, f.Recipient, f.Subject, f.Folder));
        Assert.Equal((true, "n", "e", false), (f.HasAttachment, f.AttachmentName, f.AttachmentExt, f.Unread));
    }
}
