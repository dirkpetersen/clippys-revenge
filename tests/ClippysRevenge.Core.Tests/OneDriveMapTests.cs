namespace ClippysRevenge.Core.Tests;

public class OneDriveMapTests
{
    private static readonly SyncedRoot Team = new("https://contoso.sharepoint.com/sites/Finance/Shared Documents/", @"C:\OD\Finance - Documents", "teamsite", false);
    private static readonly SyncedRoot TeamSub = new("https://contoso.sharepoint.com/sites/Finance/Shared Documents/Budget/", @"C:\OD\Budget", "teamsite", true);
    private static readonly SyncedRoot My = new("https://contoso-my.sharepoint.com/personal/jdoe_contoso_com/Documents/", @"C:\OD\OneDrive - Contoso", "mysite", false);
    private static readonly SyncedRoot Consumer = new("https://d.docs.live.net/abc123def4567890/", @"C:\OD\OneDrive", "personal", false);
    private static readonly SyncedRoot Evil = new("https://files.evil.example/", @"C:\OD\Evil", "teamsite", false);

    private const string Docs = "https://contoso.sharepoint.com/sites/Finance/Shared%20Documents";

    private static void AssertUnmappable(MappedLink m, string because = "")
    {
        var u = Assert.IsType<MappedLink.Unmappable>(m);
        Assert.Equal("not synced to this PC", u.Reason);
    }

    private static void AssertLocal(MappedLink m, SyncedRoot root, params string[] rest)
    {
        var l = Assert.IsType<MappedLink.Local>(m);
        Assert.Equal(root, l.Root);
        Assert.Equal(Path.Join([root.MountPoint, .. rest]), l.Path);
    }

    [Fact]
    public void Direct_PercentTwenty() =>
        AssertLocal(OneDriveMap.Map(Docs + "/Reports/Q4.xlsx", [Team]), Team, "Reports", "Q4.xlsx");

    [Theory]
    [InlineData("https://contoso.sharepoint.com/:x:/r/sites/Finance/Shared%20Documents/Q4.xlsx?d=w12&csf=1")]
    [InlineData("https://contoso.sharepoint.com/:X:/R/sites/Finance/Shared%20Documents/Q4.xlsx")]
    public void ShareLinkRPrefix(string url) => AssertLocal(OneDriveMap.Map(url, [Team]), Team, "Q4.xlsx");

    [Theory]
    [InlineData("https://contoso.sharepoint.com/:w:/s/Finance/EaBcDeF?e=x")]
    [InlineData("https://contoso.sharepoint.com/:f:/g/personal/x/EToken")]
    [InlineData("https://contoso.sharepoint.com/:x:/")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/_layouts/15/Doc.aspx?sourcedoc=%7Babc%7D")]
    [InlineData("https://1drv.ms/x/s!Abc")]
    [InlineData("https://onedrive.live.com/edit?id=ABC!1")]
    public void Unmappable_Forms(string url) => AssertUnmappable(OneDriveMap.Map(url, [Team, My, Consumer]));

    [Fact]
    public void PersonalViaMySite() =>
        AssertLocal(OneDriveMap.Map("https://contoso-my.sharepoint.com/personal/jdoe_contoso_com/Documents/Notes/a.docx", [Team, My]),
            My, "Notes", "a.docx");

    [Fact]
    public void LongestPrefixWinsAnyOrder()
    {
        var url = Docs + "/Budget/2027/b.xlsx";
        AssertLocal(OneDriveMap.Map(url, [Team, TeamSub]), TeamSub, "2027", "b.xlsx");
        AssertLocal(OneDriveMap.Map(url, [TeamSub, Team]), TeamSub, "2027", "b.xlsx");
    }

    [Fact]
    public void TieGoesToFirst()
    {
        var other = Team with { MountPoint = @"C:\OD\Other" };
        AssertLocal(OneDriveMap.Map(Docs + "/x.docx", [other, Team]), other, "x.docx");
    }

    [Fact]
    public void SegmentBoundary() =>
        AssertUnmappable(OneDriveMap.Map("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents2/x.docx", [Team]));

    [Fact]
    public void CaseDifferencesKeepUrlCase() =>
        AssertLocal(OneDriveMap.Map("https://CONTOSO.sharepoint.com/SITES/finance/shared%20documents/Reports/Q4.xlsx", [Team]),
            Team, "Reports", "Q4.xlsx");

    [Fact]
    public void ConsumerMaps() =>
        AssertLocal(OneDriveMap.Map("https://d.docs.live.net/ABC123DEF4567890/Documents/c.txt", [Consumer]),
            Consumer, "Documents", "c.txt");

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/%2E%2E/%2E%2E/x.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/a%5Cb.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/a%0Ab.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/a%3Ab.docx")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/CON.txt")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/name.")]
    [InlineData("https://www.dropbox.com/s/a.pdf")]
    public void Unmappable_Paths(string url) => AssertUnmappable(OneDriveMap.Map(url, [Team]));

    [Fact]
    public void NonMicrosoftRootsIgnored()
    {
        AssertUnmappable(OneDriveMap.Map("https://files.evil.example/a.docx", [Evil]));
        AssertUnmappable(OneDriveMap.Map("https://contoso.sharepoint.com/a.docx", [Evil]));
    }

    [Fact]
    public void BadRootsSkipped()
    {
        var bad1 = Team with { UrlPrefix = "not a url" };
        var bad2 = Team with { MountPoint = " " };
        var bad3 = Team with { UrlPrefix = "https://contoso.sharepoint.com/sites/Finance/../Shared Documents/" };
        AssertLocal(OneDriveMap.Map(Docs + "/a.docx", [bad1, bad2, bad3, Team]), Team, "a.docx");
    }

    [Fact]
    public void NullAndEmpty()
    {
        AssertUnmappable(OneDriveMap.Map(null, [Team]));
        AssertUnmappable(OneDriveMap.Map("", [Team]));
        AssertUnmappable(OneDriveMap.Map(Docs + "/a.docx", null));
        AssertUnmappable(OneDriveMap.Map(Docs + "/a.docx", []));
    }

    [Fact]
    public void QueryAndFragmentDropped() =>
        AssertLocal(OneDriveMap.Map(Docs + "/Q4.xlsx?web=1#x", [Team]), Team, "Q4.xlsx");
}
