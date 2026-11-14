namespace ClippysRevenge.Core.Tests;

public class RecipientsTests
{
    private const string SevenList =
        "Example, Alice Marie <alice.example@example.edu>; Sample, Bob <bob.sample@example.edu>; Sample, Carol Ann <carol.sample@example.edu>; Garcia-Example, Maria Elena <maria.garcia-example@example.edu>; O'Example, Sean Patrick <sean.oexample@example.edu>; Smith, John <john.smith@example.edu>; Sample, Ji-Woo <jiwoo.sample@example.edu>";

    [Fact]
    public void Parse_SevenRecipientList_SevenRecipientsWithCommasInNames()
    {
        var r = Recipients.Parse(SevenList);
        Assert.Equal(7, r.Count);
        Assert.Equal(new Recipient("Example, Alice Marie", "alice.example@example.edu"), r[0]);
        Assert.Equal(new Recipient("Sample, Bob", "bob.sample@example.edu"), r[1]);
        Assert.Equal(new Recipient("O'Example, Sean Patrick", "sean.oexample@example.edu"), r[4]);
        Assert.Equal(new Recipient("Sample, Ji-Woo", "jiwoo.sample@example.edu"), r[6]);
    }

    [Fact]
    public void Parse_TrailingSemicolon_Ignored()
    {
        Assert.Single(Recipients.Parse("Sample, Bob <bob.sample@example.edu>;"));
        Assert.Equal(
            new[] { new Recipient("", "a@x.org"), new Recipient("", "b@y.org") },
            Recipients.Parse("a@x.org;; ;b@y.org; "));
    }

    [Fact]
    public void Parse_BareEmail() =>
        Assert.Equal(new[] { new Recipient("", "bob.sample@example.edu") },
            Recipients.Parse("bob.sample@example.edu"));

    [Fact]
    public void Parse_QuotedName() =>
        Assert.Equal(new[] { new Recipient("Sample, Bob", "bob.sample@example.edu") },
            Recipients.Parse("\"Sample, Bob\" <bob.sample@example.edu>"));

    [Fact]
    public void Parse_AngleOnly() =>
        Assert.Equal(new[] { new Recipient("", "bob@x.org") }, Recipients.Parse("<bob@x.org>"));

    [Fact]
    public void Parse_WhitespaceAroundParts() =>
        Assert.Equal(new[] { new Recipient("Sample, Bob", "bob@x.org") },
            Recipients.Parse("  Sample, Bob   <  bob@x.org >  "));

    [Fact]
    public void Parse_Duplicates_CaseInsensitive_FirstWins() =>
        Assert.Equal(new[] { new Recipient("Bob", "Bob.Sample@x.org") },
            Recipients.Parse("Bob <Bob.Sample@x.org>; B. Sample <bob.sample@X.ORG>; bob.sample@x.org"));

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
    [InlineData("Sample, Bob <bob@x.org>; Example, Alice", "Example, Alice")]
    [InlineData("Bob <bob>", "Bob <bob>")]
    [InlineData("Bob <>", "Bob <>")]
    [InlineData("a@x.org, b@y.org", "a@x.org, b@y.org")]
    [InlineData("a@@x.org", "a@@x.org")]
    [InlineData("@x.org", "@x.org")]
    [InlineData("Bob <bob@x.org> extra", "Bob <bob@x.org> extra")]
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
        Assert.Equal("Sample, Bob <bob@x.org>; a@y.org",
            Recipients.Format(new[] { new Recipient("Sample, Bob", "bob@x.org"), new Recipient("", "a@y.org") }));
        Assert.Equal("", Recipients.Format(Array.Empty<Recipient>()));
    }

    [Fact]
    public void Format_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Recipients.Format(null!));

    [Theory]
    [InlineData(SevenList)]
    [InlineData("Sample, Bob <bob.sample@example.edu>;")]
    [InlineData("a@x.org;; ;b@y.org; ")]
    [InlineData("bob.sample@example.edu")]
    [InlineData("\"Sample, Bob\" <bob.sample@example.edu>")]
    [InlineData("<bob@x.org>")]
    [InlineData("  Sample, Bob   <  bob@x.org >  ")]
    [InlineData("Bob <Bob.Sample@x.org>; B. Sample <bob.sample@X.ORG>; bob.sample@x.org")]
    public void Format_Parse_RoundTrip(string raw)
    {
        var first = Recipients.Parse(raw);
        Assert.Equal(first, Recipients.Parse(Recipients.Format(first)));
    }

    [Theory]
    [InlineData("Example, Alice <alice@example.edu>", "Example, Alice", "alice@example.edu")]
    [InlineData("\"Q\" <q@x.org>", "Q", "q@x.org")]
    [InlineData("<q@x.org>", "", "q@x.org")]
    [InlineData(" q@x.org ", "", "q@x.org")]
    public void TryParseEntry_Valid(string entry, string name, string email)
    {
        Assert.True(Recipients.TryParseEntry(entry, out var r));
        Assert.Equal(new Recipient(name, email), r);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Bob <bob>")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void TryParseEntry_Invalid(string? entry)
    {
        Assert.False(Recipients.TryParseEntry(entry, out var r));
        Assert.Null(r);
    }
}
