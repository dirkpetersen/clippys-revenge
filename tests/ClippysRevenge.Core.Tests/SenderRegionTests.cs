namespace ClippysRevenge.Core.Tests;

public class SenderRegionTests
{
    private static string H(string date) => $"Received: from x\r\nDate: {date}\r\nFrom: a@b\r\n";
    private static string Tue(string off) => H($"Tue, 18 Nov 2025 10:00:00 {off}");

    [Theory]
    [InlineData("+0100", true, false, true)]
    [InlineData("+0200", true, false, true)]
    [InlineData("+0000", true, false, false)]
    [InlineData("+0300", true, false, false)]
    [InlineData("-0500", false, false, false)]
    [InlineData("+0530", false, false, false)]
    [InlineData(null, true, false, false)]
    public void DecisionTable(string? off, bool de, bool za, bool com)
    {
        var h = off is null ? null : Tue(off);
        Assert.Equal(de, SenderRegion.IsEurope("a@b.de", h));
        Assert.Equal(za, SenderRegion.IsEurope("a@b.za", h));
        Assert.Equal(com, SenderRegion.IsEurope("a@b.com", h));
    }

    [Fact]
    public void NegativeZeroIsUnknown()
    {
        Assert.Null(SenderRegion.DateOffset("Tue, 18 Nov 2025 10:00:00 -0000"));
        Assert.False(SenderRegion.IsEurope("a@b.com", Tue("-0000")));
        Assert.True(SenderRegion.IsEurope("a@b.de", Tue("-0000")));
    }

    [Theory]
    [InlineData("GMT")]
    [InlineData("gmt")]
    [InlineData("UT")]
    [InlineData("UTC")]
    [InlineData("Z")]
    public void NamedZero(string z)
    {
        Assert.Equal(TimeSpan.Zero, SenderRegion.DateOffset($"Tue, 18 Nov 2025 10:00:00 {z}"));
        Assert.False(SenderRegion.IsEurope("a@b.com", Tue(z)));
        Assert.True(SenderRegion.IsEurope("a@b.de", Tue(z)));
    }

    [Fact]
    public void TrailingComment()
    {
        var v = "Tue, 18 Nov 2025 10:00:00 +0100 (CET)";
        Assert.Equal(TimeSpan.FromHours(1), SenderRegion.DateOffset(v));
        Assert.True(SenderRegion.IsEurope("a@b.org", H(v)));
    }

    [Fact]
    public void HeadersWithoutDate()
    {
        const string h = "Received: from x\r\nFrom: a@b\r\n";
        Assert.True(SenderRegion.IsEurope("a@b.de", h));
        Assert.False(SenderRegion.IsEurope("a@b.com", h));
    }

    [Theory]
    [InlineData("a@mail.example.co.uk", SenderTld.European)]
    [InlineData("a@b.eu", SenderTld.European)]
    [InlineData("A@B.DE", SenderTld.European)]
    [InlineData("a@b.de.", SenderTld.European)]
    [InlineData("a@b.io", SenderTld.OtherCountry)]
    [InlineData("a@b.za", SenderTld.OtherCountry)]
    [InlineData("a@b.xn--p1ai", SenderTld.Generic)]
    [InlineData("a@b.com", SenderTld.Generic)]
    [InlineData("/o=ExchangeLabs/ou=x", SenderTld.Generic)]
    [InlineData(null, SenderTld.Generic)]
    public void TldClasses(string? smtp, SenderTld expected) => Assert.Equal(expected, SenderRegion.Tld(smtp));

    [Fact]
    public void EuTldWithNoHeaders() => Assert.True(SenderRegion.IsEurope("a@b.eu", null));

    [Fact]
    public void ZaWithPlusTwoIsFalse() => Assert.False(SenderRegion.IsEurope("a@b.za", Tue("+0200")));

    [Theory]
    [InlineData("Tue, 18 Nov 2025 10:00:00 +1500")]
    [InlineData("Tue, 18 Nov 2025 10:00:00 +0160")]
    [InlineData("Tue, 18 Nov 2025 10:00:00 +100")]
    [InlineData("Tue, 18 Nov 2025 10:00:00 EST")]
    [InlineData("")]
    [InlineData("(comment only)")]
    [InlineData(null)]
    public void InvalidOffsets(string? v) => Assert.Null(SenderRegion.DateOffset(v));

    [Fact]
    public void MaxOffset() => Assert.Equal(TimeSpan.FromHours(14), SenderRegion.DateOffset("Tue, 18 Nov 2025 10:00:00 +1400"));

    [Fact]
    public void EuropeanTldsContainsExpected()
    {
        Assert.Contains("de", SenderRegion.EuropeanTlds);
        Assert.Contains("uk", SenderRegion.EuropeanTlds);
        Assert.DoesNotContain("us", SenderRegion.EuropeanTlds);
    }

    [Fact]
    public void CentralEuropeZone()
    {
        var z = SenderRegion.CentralEurope();
        Assert.NotNull(z);
        Assert.Equal(TimeSpan.FromHours(1), z.GetUtcOffset(new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(TimeSpan.FromHours(2), z.GetUtcOffset(new DateTimeOffset(2025, 7, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Same(z, SenderRegion.CentralEurope());
    }
}
