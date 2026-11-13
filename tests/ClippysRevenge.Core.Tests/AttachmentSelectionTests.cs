using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class AttachmentSelectionTests
{
    private static AttachmentInfo A(int pos, string name, bool inline = false) => new(pos, name, 1, inline);

    private static readonly IReadOnlyList<AttachmentInfo> L =
        [A(1, "report.pdf"), A(2, "image001.png", true), A(3, "Report.PDF"), A(4, "data.xlsx")];

    private static int[] Pos(IReadOnlyList<AttachmentInfo> l) => l.Select(a => a.Position).ToArray();

    [Fact]
    public void S1_Empty()
    {
        var ex = Assert.Throws<McpException>(() => AttachmentSelection.SelectOne([], null, null));
        Assert.Equal("This email has no attachments.", ex.Message);
    }

    [Fact]
    public void S2_S3_ByPosition()
    {
        Assert.Equal("data.xlsx", AttachmentSelection.SelectOne(L, null, 4).FileName);
        Assert.Equal("image001.png", AttachmentSelection.SelectOne(L, null, 2).FileName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void S4_BadPosition(int p)
    {
        var ex = Assert.Throws<McpException>(() => AttachmentSelection.SelectOne(L, null, p));
        Assert.StartsWith($"There is no attachment at position {p}.", ex.Message);
        Assert.Contains("This email has 4 attachment(s): 1 \"report.pdf\", 2 \"image001.png\" (inline), 3 \"Report.PDF\", 4 \"data.xlsx\".", ex.Message);
    }

    [Theory]
    [InlineData("data.xlsx")]
    [InlineData(" DATA.XLSX ")]
    public void S5_ByName(string n) => Assert.Equal(4, AttachmentSelection.SelectOne(L, n, null).Position);

    [Fact]
    public void S6_Ambiguous()
    {
        var ex = Assert.Throws<McpException>(() => AttachmentSelection.SelectOne(L, "report.pdf", null));
        Assert.Equal("2 attachments are named \"report.pdf\" (positions 1, 3). Pass position to choose one.", ex.Message);
    }

    [Fact]
    public void S7_NoSuchName()
    {
        var ex = Assert.Throws<McpException>(() => AttachmentSelection.SelectOne(L, "nope.txt", null));
        Assert.StartsWith("This email has no attachment named \"nope.txt\". Its attachments: 1 \"report.pdf\"", ex.Message);
    }

    [Fact]
    public void S8_NameAndPositionAgree() => Assert.Equal(3, AttachmentSelection.SelectOne(L, "report.pdf", 3).Position);

    [Fact]
    public void S9_NameAndPositionDisagree()
    {
        var ex = Assert.Throws<McpException>(() => AttachmentSelection.SelectOne(L, "data.xlsx", 1));
        Assert.Equal("The attachment at position 1 is \"report.pdf\", not \"data.xlsx\". Pass only one of filename and position, or matching values.", ex.Message);
    }

    [Fact]
    public void S10_Neither_Many()
    {
        var ex = Assert.Throws<McpException>(() => AttachmentSelection.SelectOne(L, null, null));
        Assert.StartsWith("This email has 4 attachments. Pass filename or position to choose one: ", ex.Message);
    }

    [Fact]
    public void S11_SingleNonInline() =>
        Assert.Equal("a.pdf", AttachmentSelection.SelectOne([A(1, "a.pdf"), A(2, "logo.png", true)], null, null).FileName);

    [Fact]
    public void S12_OnlyInline() =>
        Assert.Equal("logo.png", AttachmentSelection.SelectOne([A(1, "logo.png", true)], null, null).FileName);

    [Fact]
    public void S13_TwoInline()
    {
        var ex = Assert.Throws<McpException>(() =>
            AttachmentSelection.SelectOne([A(1, "a.png", true), A(2, "b.png", true)], null, null));
        Assert.Contains("Pass filename or position", ex.Message);
    }

    [Fact]
    public void S14_BlankNameIgnored() => Assert.Equal(4, AttachmentSelection.SelectOne(L, "   ", 4).Position);

    [Fact]
    public void S15_S16_Inline()
    {
        Assert.Equal([1, 3, 4], Pos(AttachmentSelection.SelectForBulk(L, null, null, false)));
        Assert.Equal([1, 2, 3, 4], Pos(AttachmentSelection.SelectForBulk(L, null, null, true)));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".PDF")]
    [InlineData(" pdf ")]
    public void S17_Ext(string e) => Assert.Equal([1, 3], Pos(AttachmentSelection.SelectForBulk(L, null, e, false)));

    [Fact]
    public void S18_Name()
    {
        Assert.Empty(AttachmentSelection.SelectForBulk(L, "image", null, false));
        Assert.Equal([2], Pos(AttachmentSelection.SelectForBulk(L, "image", null, true)));
    }

    [Fact]
    public void S19_BothOnSameAttachment() => Assert.Empty(AttachmentSelection.SelectForBulk(L, "report", "xlsx", false));

    [Fact]
    public void S20_BlankFilters()
    {
        Assert.Equal([1, 3, 4], Pos(AttachmentSelection.SelectForBulk(L, null, ".", false)));
        Assert.Equal([1, 3, 4], Pos(AttachmentSelection.SelectForBulk(L, "", "", false)));
    }

    [Fact]
    public void S21_ExtNeedsDot() => Assert.Empty(AttachmentSelection.SelectForBulk([A(1, "pdf")], null, "pdf", false));

    [Fact]
    public void S22_AsciiFolding()
    {
        Assert.Empty(AttachmentSelection.SelectForBulk([A(1, "ÄNDERUNG.pdf")], "änderung", null, false));
        Assert.Equal([1], Pos(AttachmentSelection.SelectForBulk([A(1, "ÄNDERUNG.pdf")], "NDERUNG", null, false)));
    }

    [Fact]
    public void S23_LiteralWildcards() =>
        Assert.Equal([1], Pos(AttachmentSelection.SelectForBulk([A(1, "50%_off.pdf"), A(2, "50xxoff.pdf")], "50%_", null, false)));

    [Fact]
    public void S24_Ordered() =>
        Assert.Equal([1, 3], Pos(AttachmentSelection.SelectForBulk([A(3, "c.pdf"), A(1, "a.pdf")], null, null, false)));

    [Fact]
    public void S25_S28_MatchLive()
    {
        Assert.Equal(1, AttachmentSelection.MatchLive(L, A(1, "report.pdf"))!.Position);
        Assert.Null(AttachmentSelection.MatchLive(L, A(4, "REPORT.pdf")));
        Assert.Equal(2, AttachmentSelection.MatchLive([A(1, "x.doc"), A(2, "data.xlsx")], A(4, "data.xlsx"))!.Position);
        Assert.Null(AttachmentSelection.MatchLive([A(1, "x.doc")], A(1, "y.doc")));
    }

    [Fact]
    public void S29_S30_Describe()
    {
        var many = Enumerable.Range(1, 25).Select(i => A(i, $"f{i}.txt")).ToList();
        var d = AttachmentSelection.Describe(many);
        Assert.EndsWith("20 \"f20.txt\", … (5 more)", d);
        Assert.DoesNotContain("f21.txt", d);
        Assert.Equal("(none)", AttachmentSelection.Describe([]));
    }

    [Fact]
    public void S31_Nulls()
    {
        Assert.Throws<ArgumentNullException>(() => AttachmentSelection.SelectOne(null!, null, null));
        Assert.Throws<ArgumentNullException>(() => AttachmentSelection.SelectForBulk(null!, null, null, false));
        Assert.Throws<ArgumentNullException>(() => AttachmentSelection.MatchLive(null!, A(1, "a")));
        Assert.Throws<ArgumentNullException>(() => AttachmentSelection.Describe(null!));
    }
}
