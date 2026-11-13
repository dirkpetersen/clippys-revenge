using ClippysRevenge.Core;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public class StoreCatalogTests
{
    private static StoreCandidate C(string name, string smtp, int type, bool def = false) =>
        new(name, "id-" + name, smtp, type, def);

    [Fact]
    public void Filter_keeps_only_exchange_mailboxes_and_marks_own()
    {
        var list = StoreCatalog.Filter([
            C("Me", "Me@X.org", StoreCatalog.PrimaryExchangeMailbox, true),
            C("Public", "", 2),
            C("Pst", "", 3),
            C("Help", " Help@x.org ", StoreCatalog.AdditionalExchangeMailbox),
            C("Other", "o@x.org", StoreCatalog.ExchangeMailbox),
        ]);
        Assert.Equal(["Me", "Help", "Other"], list.Select(s => s.DisplayName));
        Assert.True(list[0].IsOwn);
        Assert.Equal("me@x.org", list[0].Smtp);
        Assert.Equal("help@x.org", list[1].Smtp);
        Assert.False(list[1].IsOwn);
    }

    [Fact]
    public void Only_the_default_store_is_own_when_two_primary_mailboxes_exist()
    {
        var all = StoreCatalog.Filter([C("Other", "o@x.org", 0), C("Me", "me@x.org", 0, true)]);
        Assert.False(all[0].IsOwn);
        Assert.True(all[1].IsOwn);
        Assert.Equal("Me", StoreCatalog.Find(all, "")!.DisplayName);
        Assert.Equal(["o@x.org"], StoreCatalog.SharedOnly(all).Select(s => s.Smtp));
    }

    [Fact]
    public void First_primary_mailbox_is_own_when_default_store_is_not_a_mailbox()
    {
        var all = StoreCatalog.Filter([C("Pst", "", 3, true), C("A", "a@x.org", 0), C("B", "b@x.org", 0)]);
        Assert.Equal(["A"], all.Where(s => s.IsOwn).Select(s => s.DisplayName));
    }

    [Fact]
    public void SharedOnly_excludes_own_and_unknown_smtp()
    {
        var all = StoreCatalog.Filter([
            C("Me", "me@x.org", 0, true), C("A", "a@x.org", 4), C("B", null!, 4)]);
        Assert.Equal(["a@x.org"], StoreCatalog.SharedOnly(all).Select(s => s.Smtp));
    }

    [Fact]
    public void Find_resolves_own_and_shared_case_insensitively()
    {
        var all = StoreCatalog.Filter([C("Me", "me@x.org", 0, true), C("A", "a@x.org", 4)]);
        Assert.Equal("Me", StoreCatalog.Find(all, "")!.DisplayName);
        Assert.Equal("Me", StoreCatalog.Find(all, null)!.DisplayName);
        Assert.Equal("A", StoreCatalog.Find(all, " A@X.org ")!.DisplayName);
        Assert.Null(StoreCatalog.Find(all, "me@x.org"));
        Assert.Null(StoreCatalog.Find(all, "zz@x.org"));
    }

    [Fact]
    public void NotAvailable_lists_configured_mailboxes()
    {
        var ex = StoreCatalog.NotAvailable("nope@x.org", ["", "a@x.org"]);
        Assert.Contains("not configured", ex.Message);
        Assert.Contains("\"a@x.org\"", ex.Message);
        Assert.Contains("your own mailbox", ex.Message);
    }

    [Fact]
    public void NotAvailable_for_configured_but_unmounted_says_so()
    {
        var ex = StoreCatalog.NotAvailable("A@x.org", ["", "a@x.org"]);
        Assert.IsType<McpException>(ex);
        Assert.Contains("not open in Outlook", ex.Message);
    }

    [Theory]
    [InlineData("a@x.org", true)]
    [InlineData(" a@x.org ", true)]
    [InlineData("a@@x.org", false)]
    [InlineData("@x.org", false)]
    [InlineData("a@", false)]
    [InlineData("a b@x.org", false)]
    [InlineData("a,b@x.org", false)]
    [InlineData("a(b)@x.org", false)]
    [InlineData(null, false)]
    public void EmailAddress_IsValid(string? s, bool ok) => Assert.Equal(ok, EmailAddress.IsValid(s));
}
