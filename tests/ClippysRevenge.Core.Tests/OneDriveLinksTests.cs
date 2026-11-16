namespace ClippysRevenge.Core.Tests;

public class OneDriveLinksTests
{
    [Theory]
    [InlineData("https://contoso.sharepoint.com/x", true)]
    [InlineData("https://contoso-my.sharepoint.com/x", true)]
    [InlineData("https://CONTOSO.SharePoint.com./x", true)]
    [InlineData("https://contoso.sharepoint.com:443/x", true)]
    [InlineData("https://onedrive.live.com/x", true)]
    [InlineData("https://d.docs.live.net/x", true)]
    [InlineData("https://1drv.ms/x", true)]
    [InlineData("https://sharepoint.com/x", false)]
    [InlineData("https://contoso.sharepoint.com.evil.example/x", false)]
    [InlineData("https://evilsharepoint.com/x", false)]
    [InlineData("https://contoso.sharepoint.us/x", false)]
    [InlineData("https://contoso.sharepoint.cn/x", false)]
    [InlineData("https://contoso.sharepoint.de/x", false)]
    [InlineData("https://www.dropbox.com/x", false)]
    [InlineData("https://drive.google.com/x", false)]
    [InlineData("https://contoso.sharepoint-df.com/x", false)]
    [InlineData("https://www.office.com/x", false)]
    [InlineData("https://login.live.com/x", false)]
    [InlineData("http://contoso.sharepoint.com/x", false)]
    [InlineData("https://user@contoso.sharepoint.com/x", false)]
    [InlineData("https://contoso.sharepoint.com:8443/x", false)]
    [InlineData("https://13.107.136.9/x", false)]
    [InlineData("https://[2603:1006::1]/x", false)]
    public void HostRule(string url, bool expected)
    {
        Assert.Equal(expected, OneDriveLinks.IsMicrosoftFileHost(new Uri(url)));
    }

    [Fact]
    public void HostRule_RelativeFalse_NullThrows()
    {
        Assert.False(OneDriveLinks.IsMicrosoftFileHost(new Uri("/x", UriKind.Relative)));
        Assert.Throws<ArgumentNullException>(() => OneDriveLinks.IsMicrosoftFileHost(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Find_NullOrEmpty(string? body) => Assert.Empty(OneDriveLinks.Find(body));

    [Fact]
    public void Find_DirectLinkName()
    {
        var l = Assert.Single(OneDriveLinks.Find(
            "See https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Q4%20Plan.docx."));
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Q4%20Plan.docx", l.Url);
        Assert.Equal("Q4 Plan.docx", l.Name);
    }

    [Fact]
    public void Find_TrailingPunctuation()
    {
        var r = OneDriveLinks.Find("(https://contoso.sharepoint.com/a/b.xlsx), and https://contoso.sharepoint.com/a/c.pdf!?");
        Assert.Equal(["https://contoso.sharepoint.com/a/b.xlsx", "https://contoso.sharepoint.com/a/c.pdf"],
            r.Select(x => x.Url).ToArray());
    }

    [Fact]
    public void Find_EndsAtQuoteAndAngle()
    {
        var l = Assert.Single(OneDriveLinks.Find("<a href=\"https://contoso.sharepoint.com/a/d.pdf\">"));
        Assert.Equal("https://contoso.sharepoint.com/a/d.pdf", l.Url);
    }

    [Fact]
    public void Find_TextAngleUrlName()
    {
        var l = Assert.Single(OneDriveLinks.Find("Please check Budget 2027<https://contoso.sharepoint.com/:x:/s/Finance/EaBcD?e=1>"));
        Assert.Equal("Please check Budget 2027", l.Name);
    }

    [Fact]
    public void Find_TwoOnALine()
    {
        var r = OneDriveLinks.Find("Plan<https://contoso.sharepoint.com/:w:/s/a/X> and Budget<https://contoso.sharepoint.com/:x:/s/a/Y>");
        Assert.Equal(["Plan", "and Budget"], r.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Find_LongTextNameCut()
    {
        var text = new string('a', 100) + new string('b', 100);
        var l = Assert.Single(OneDriveLinks.Find(text + "<https://contoso.sharepoint.com/:w:/g/T>"));
        Assert.True(l.Name.Length <= OneDriveLinks.MaxNameChars);
        Assert.EndsWith(new string('b', 100), l.Name);
    }

    [Fact]
    public void Find_HostFallbackNames()
    {
        var l = Assert.Single(OneDriveLinks.Find("https://contoso.sharepoint.com/:w:/s/Finance/EToken"));
        Assert.Equal("a shared file on contoso.sharepoint.com", l.Name);
        var d = Assert.Single(OneDriveLinks.Find("https://1drv.ms/w/s!AbC"));
        Assert.Equal("a shared file on 1drv.ms", d.Name);
    }

    [Fact]
    public void Find_ExternalOnlyEmpty_MixedKeepsMicrosoft()
    {
        Assert.Empty(OneDriveLinks.Find("https://www.dropbox.com/s/x/a.pdf https://drive.google.com/file/d/1"));
        var l = Assert.Single(OneDriveLinks.Find("https://www.dropbox.com/s/x/a.pdf then https://contoso.sharepoint.com/a/b.pdf"));
        Assert.Equal("https://contoso.sharepoint.com/a/b.pdf", l.Url);
    }

    private const string Wrap = "https://nam12.safelinks.protection.outlook.com/?url=";

    [Fact]
    public void Find_SafeLinksKept()
    {
        var l = Assert.Single(OneDriveLinks.Find(Wrap
            + "https%3A%2F%2Fcontoso.sharepoint.com%2Fsites%2FF%2FShared%2520Documents%2Fa.docx&data=05"));
        Assert.StartsWith("https://contoso.sharepoint.com/", l.Url);
        Assert.Equal("a.docx", l.Name);
    }

    [Theory]
    [InlineData("https://nam12.safelinks.protection.outlook.com/?url=https%3A%2F%2Fwww.dropbox.com%2Fs%2Fx")]
    [InlineData("https://nam12.safelinks.protection.outlook.com/?url=https%3A%2F%2Feur01.safelinks.protection.outlook.com%2F%3Furl%3Dhttps%253A%252F%252Fcontoso.sharepoint.com%252Fa")]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?data=1")]
    [InlineData("https://nam12.safelinks.protection.outlook.com/?url=http%3A%2F%2Fcontoso.sharepoint.com%2Fa")]
    public void Find_SafeLinksDropped(string body) => Assert.Empty(OneDriveLinks.Find(body));

    [Fact]
    public void Find_Dedupe()
    {
        var a = "https://contoso.sharepoint.com/a/x.docx";
        var r = OneDriveLinks.Find($"{a} {a} {a} https://contoso.sharepoint.com/a/y.docx");
        Assert.Equal(2, r.Count);
        Assert.Single(OneDriveLinks.Find($"{a}#p1 {a}#p2"));
    }

    [Fact]
    public void Find_CapAfterFilter()
    {
        var body = string.Join(' ', Enumerable.Range(0, 5).Select(i => $"https://www.dropbox.com/s/{i}"))
            + " " + string.Join(' ', Enumerable.Range(0, 12).Select(i => $"https://contoso.sharepoint.com/a/f{i}.pdf"));
        var r = OneDriveLinks.Find(body);
        Assert.Equal(10, r.Count);
        Assert.Equal("f0.pdf", r[0].Name);
        Assert.Equal("f9.pdf", r[9].Name);
    }

    [Fact]
    public void Find_UpperCaseScheme() =>
        Assert.Single(OneDriveLinks.Find("HTTPS://contoso.sharepoint.com/a/b.pdf"));

    [Fact]
    public void TryParse_Cloud()
    {
        Assert.True(OneDriveLinks.TryParse("https://contoso.sharepoint.com/:x:/r/sites/F/Shared%20Documents/c.xlsx?d=w1", out var l));
        Assert.Equal("c.xlsx", l.Name);
    }

    [Theory]
    [InlineData("https://www.dropbox.com/s/a/b.pdf")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void TryParse_Refused(string? url)
    {
        Assert.False(OneDriveLinks.TryParse(url, out var l));
        Assert.Null(l);
    }
}
