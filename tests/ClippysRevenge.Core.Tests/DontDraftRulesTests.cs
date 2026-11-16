using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class DontDraftRulesTests
{
    [Theory]
    [InlineData(" Ann@Contoso.Example ", "Ann@Contoso.Example")]
    [InlineData("/O=EXCHANGELABS/OU=X/CN=RECIPIENTS/CN=ANN", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("a@b@c.example", null)]
    [InlineData("ann @x.example", null)]
    public void SenderAddress(string? input, string? expected) => Assert.Equal(expected, DontDraftRules.SenderAddress(input));

    [Theory]
    [InlineData("Ann@Mail.Contoso.Example", "mail.contoso.example")]
    [InlineData("x@localhost", null)]
    [InlineData("x@-bad.example", null)]
    [InlineData(null, null)]
    public void Domain(string? input, string? expected) => Assert.Equal(expected, DontDraftRules.Domain(input));

    [Theory]
    [InlineData("ME@example.org", "me@example.org", "", true)]
    [InlineData("helpdesk@example.org", "me@example.org", "helpdesk@example.org", true)]
    [InlineData("ann@contoso.example", "me@example.org", "", false)]
    [InlineData("ann@contoso.example", "", "", false)]
    public void IsOwnAddress(string a, string own, string mb, bool expected) =>
        Assert.Equal(expected, DontDraftRules.IsOwnAddress(a, own, mb));

    [Theory]
    [InlineData("cs.example.edu", true)]
    [InlineData("example.edu", true)]
    [InlineData("lab.cs.example.edu", true)]
    [InlineData("xcs.example.edu", false)]
    [InlineData("s.example.edu", false)]
    [InlineData("example.org", false)]
    public void IsOwnDomain(string domain, bool expected) =>
        Assert.Equal(expected, DontDraftRules.IsOwnDomain(domain, "me@cs.example.edu"));

    [Fact]
    public void IsOwnDomain_without_own_address_is_false() => Assert.False(DontDraftRules.IsOwnDomain("example.edu", null));

    [Fact]
    public void AddressCovered_cases()
    {
        Assert.True(DontDraftRules.AddressCovered(["contoso.example"], "ann@mail.contoso.example"));
        Assert.True(DontDraftRules.AddressCovered(["@contoso.example"], "ann@mail.contoso.example"));
        Assert.True(DontDraftRules.AddressCovered(["ANN@contoso.example"], "ann@contoso.example"));
        Assert.False(DontDraftRules.AddressCovered(["not valid!!", null], "ann@contoso.example"));
        Assert.False(DontDraftRules.AddressCovered(null, "ann@contoso.example"));
    }

    [Fact]
    public void DomainCovered_cases()
    {
        Assert.True(DontDraftRules.DomainCovered(["contoso.example"], "mail.contoso.example"));
        Assert.False(DontDraftRules.DomainCovered(["mail.contoso.example"], "contoso.example"));
        Assert.False(DontDraftRules.DomainCovered(["ann@contoso.example"], "contoso.example"));
        Assert.False(DontDraftRules.DomainCovered(["not valid!!", null], "contoso.example"));
        Assert.False(DontDraftRules.DomainCovered(null, "contoso.example"));
    }

    [Fact]
    public void IsFull_cases()
    {
        Assert.False(DontDraftRules.IsFull(Enumerable.Range(0, 199).Select(i => "u" + i + "@o.example").ToList()));
        Assert.True(DontDraftRules.IsFull(Enumerable.Range(0, 200).Select(i => "u" + i + "@o.example").ToList()));
        Assert.False(DontDraftRules.IsFull(null));
    }

    [Fact]
    public void Texts_are_exact()
    {
        Assert.Equal("I couldn't find that sender's email address.", DontDraftTexts.NoAddress);
        Assert.Equal("That's your own address.", DontDraftTexts.OwnAddress);
        Assert.Equal("That's your own organization's domain. To stop drafting for all internal mail, add it in Settings → AI drafts.", DontDraftTexts.OwnDomain);
        Assert.Equal("\"Never draft for\" already has 200 entries. Remove some in Settings → AI drafts.", DontDraftTexts.Full);
        Assert.Equal("Add", DontDraftTexts.AddButton);
        Assert.Equal("Cancel", DontDraftTexts.CancelButton);
        Assert.Equal("x@y.example is already on \"Never draft for\".", DontDraftTexts.Already("x@y.example"));
        Assert.Equal("Never draft replies to anyone at contoso.example?", DontDraftTexts.ConfirmHeading("contoso.example"));
        Assert.Equal("I won't draft replies to a@b.example any more. You can undo this in Settings → AI drafts.",
            DontDraftTexts.Added(DontDraftKind.Sender, "a@b.example"));
        Assert.Equal("I won't draft replies to anyone at b.example any more. You can undo this in Settings → AI drafts.",
            DontDraftTexts.Added(DontDraftKind.Domain, "b.example"));
        Assert.Equal("AI drafts: added an entry to \"Never draft for\" (1 entry).", DontDraftTexts.AddedEvent(1));
        Assert.Equal("AI drafts: added an entry to \"Never draft for\" (3 entries).", DontDraftTexts.AddedEvent(3));
        Assert.Equal("Don't draft for this sender", AiMenuTexts.DontDraftSender);
        Assert.Equal("Don't draft for this sender's domain…", AiMenuTexts.DontDraftDomain);
    }
}
