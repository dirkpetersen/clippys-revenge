namespace ClippysRevenge.Core.Tests;

public class RecipientsTests
{
    private const string Osu =
        "Thompson, Christopher Scott <chris.thompson@oregonstate.edu>; Petersen, Dirk <dirk.petersen@oregonstate.edu>; Nguyen, Anh Thi <anh.nguyen@oregonstate.edu>; Garcia-Lopez, Maria Elena <maria.garcia-lopez@oregonstate.edu>; O'Brien, Sean Patrick <sean.obrien@oregonstate.edu>; Smith, John <john.smith@oregonstate.edu>; Kim, Ji-Woo <jiwoo.kim@oregonstate.edu>";

    [Fact]
    public void Parse_OsuList_SevenRecipientsWithCommasInNames()
    {
        var r = Recipients.Parse(Osu);
        Assert.Equal(7, r.Count);
        Assert.Equal(new Recipient("Thompson, Christopher Scott", "chris.thompson@oregonstate.edu"), r[0]);
        Assert.Equal(new Recipient("Petersen, Dirk", "dirk.petersen@oregonstate.edu"), r[1]);
        Assert.Equal(new Recipient("O'Brien, Sean Patrick", "sean.obrien@oregonstate.edu"), r[4]);
        Assert.Equal(new Recipient("Kim, Ji-Woo", "jiwoo.kim@oregonstate.edu"), r[6]);
    }

    [Fact]
    public void Parse_TrailingSemicolon_Ignored()
    {
        Assert.Single(Recipients.Parse("Petersen, Dirk <dirk.petersen@oregonstate.edu>;"));
        Assert.Equal(
            new[] { new Recipient("", "a@x.org"), new Recipient("", "b@y.org") },
            Recipients.Parse("a@x.org;; ;b@y.org; "));
    }

    [Fact]
    public void Parse_BareEmail() =>
        Assert.Equal(new[] { new Recipient("", "dirk.petersen@oregonstate.edu") },
            Recipients.Parse("dirk.petersen@oregonstate.edu"));

    [Fact]
    public void Parse_QuotedName() =>
        Assert.Equal(new[] { new Recipient("Petersen, Dirk", "dirk.petersen@oregonstate.edu") },
            Recipients.Parse("\"Petersen, Dirk\" <dirk.petersen@oregonstate.edu>"));

    [Fact]
    public void Parse_AngleOnly() =>
        Assert.Equal(new[] { new Recipient("", "dirk@x.org") }, Recipients.Parse("<dirk@x.org>"));

    [Fact]
    public void Parse_WhitespaceAroundParts() =>
        Assert.Equal(new[] { new Recipient("Petersen, Dirk", "dirk@x.org") },
            Recipients.Parse("  Petersen, Dirk   <  dirk@x.org >  "));

    [Fact]
    public void Parse_Duplicates_CaseInsensitive_FirstWins() =>
        Assert.Equal(new[] { new Recipient("Dirk", "Dirk.Petersen@x.org") },
            Recipients.Parse("Dirk <Dirk.Petersen@x.org>; D. Petersen <dirk.petersen@X.ORG>; dirk.petersen@x.org"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;")]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty(string raw) =>
        Assert.Empty(Recipients.Parse(raw));

    [Fact]
    public void Parse_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Recipients.Parse(null!));

    [Theory]
    [InlineData("Petersen, Dirk <dirk@x.org>; Thompson, Chris", "Thompson, Chris")]
    [InlineData("Dirk <dirk>", "Dirk <dirk>")]
    [InlineData("Dirk <>", "Dirk <>")]
    [InlineData("a@x.org, b@y.org", "a@x.org, b@y.org")]
    [InlineData("a@@x.org", "a@@x.org")]
    [InlineData("@x.org", "@x.org")]
    [InlineData("Dirk <dirk@x.org> extra", "Dirk <dirk@x.org> extra")]
    public void Parse_MissingEmail_ThrowsQuotingEntry(string raw, string entry)
    {
        var ex = Assert.Throws<FormatException>(() => Recipients.Parse(raw));
        Assert.Equal(
            $"Recipient \"{entry}\" has no valid email address. Use \"Name <email>\" or a bare email, and separate recipients with \";\".",
            ex.Message);
    }

    [Fact]
    public void Format_Basic()
    {
        Assert.Equal("Petersen, Dirk <dirk@x.org>; a@y.org",
            Recipients.Format(new[] { new Recipient("Petersen, Dirk", "dirk@x.org"), new Recipient("", "a@y.org") }));
        Assert.Equal("", Recipients.Format(Array.Empty<Recipient>()));
    }

    [Fact]
    public void Format_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Recipients.Format(null!));

    [Theory]
    [InlineData(Osu)]
    [InlineData("Petersen, Dirk <dirk.petersen@oregonstate.edu>;")]
    [InlineData("a@x.org;; ;b@y.org; ")]
    [InlineData("dirk.petersen@oregonstate.edu")]
    [InlineData("\"Petersen, Dirk\" <dirk.petersen@oregonstate.edu>")]
    [InlineData("<dirk@x.org>")]
    [InlineData("  Petersen, Dirk   <  dirk@x.org >  ")]
    [InlineData("Dirk <Dirk.Petersen@x.org>; D. Petersen <dirk.petersen@X.ORG>; dirk.petersen@x.org")]
    public void Format_Parse_RoundTrip(string raw)
    {
        var first = Recipients.Parse(raw);
        Assert.Equal(first, Recipients.Parse(Recipients.Format(first)));
    }
}
