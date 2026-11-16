namespace ClippysRevenge.Core.Tests;

public class AiAttachmentComTests
{
    [Theory]
    [InlineData(7, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(null, 4, true)]
    [InlineData(null, 5, false)]
    [InlineData(null, 1, false)]
    [InlineData("7", 1, false)]
    public void IsCloud(object? method, int type, bool expected) =>
        Assert.Equal(expected, AiAttachmentCom.IsCloud(method, type));

    [Fact]
    public void IsCloud_LongSeven() => Assert.True(AiAttachmentCom.IsCloud(7L, 1));

    [Fact]
    public void CloudUrl_PicksTheFirstAbsoluteHttpsString()
    {
        Assert.Equal("https://contoso.sharepoint.com/sites/x/a.docx",
            AiAttachmentCom.CloudUrl("https://contoso.sharepoint.com/sites/x/a.docx", null));
        Assert.Equal("https://contoso.sharepoint.com/b",
            AiAttachmentCom.CloudUrl("report.docx", " https://contoso.sharepoint.com/b "));
        Assert.Null(AiAttachmentCom.CloudUrl("http://contoso.sharepoint.com/a", null));
        Assert.Null(AiAttachmentCom.CloudUrl(42, "ftp://x/y"));
        Assert.Null(AiAttachmentCom.CloudUrl("/a/b", null));
        Assert.Null(AiAttachmentCom.CloudUrl(null, null));
        Assert.Null(AiAttachmentCom.CloudUrl("https://", null));
    }

    [Fact]
    public void Item_BuildsFrom()
    {
        var t = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var i = AiAttachmentCom.Item("S", " Ann ", " ann@x.org ", t, "body", ["a.pdf"]);
        Assert.Equal("Ann <ann@x.org>", i.From);
        Assert.Equal("S", i.Subject);
        Assert.Equal(t, i.Sent);
        Assert.Equal("body", i.Body);
        Assert.Equal(["a.pdf"], i.AttachmentNames);
        Assert.Equal("Ann", AiAttachmentCom.Item(null, "Ann", " ", null, null, null).From);
        Assert.Equal("ann@x.org", AiAttachmentCom.Item(null, null, "ann@x.org", null, null, null).From);
        Assert.Null(AiAttachmentCom.Item(null, " ", "", null, null, null).From);
    }

    [Fact]
    public void Item_CutsTheBody()
    {
        Assert.Equal(20_000, AiAttachmentCom.Item(null, null, null, null, new string('a', 25_000), null).Body!.Length);
        var split = new string('a', 19_999) + "😀" + "more text";
        var cut = AiAttachmentCom.Item(null, null, null, null, split, null).Body!;
        Assert.Equal(19_999, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
        Assert.Null(AiAttachmentCom.Item(null, null, null, null, null, null).Body);
    }

    [Fact]
    public void Item_LimitsNames()
    {
        var names = Enumerable.Range(0, 60).Select(i => "f" + i).ToList();
        var got = AiAttachmentCom.Item(null, null, null, null, null, names).AttachmentNames;
        Assert.Equal(names.Take(50), got);
        Assert.Empty(AiAttachmentCom.Item(null, null, null, null, null, null).AttachmentNames);
    }
}
