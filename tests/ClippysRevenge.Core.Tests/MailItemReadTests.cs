using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public class MailItemReadTests
{
    private static readonly COMException Generic = new("x", unchecked((int)0x80004005));

    [Fact]
    public void ComSafe_behaviour()
    {
        Assert.Equal("v", ComSafe.Try(() => "v"));
        Assert.Null(ComSafe.Try<string>(() => throw Generic));
        Assert.Equal(0, ComSafe.Try<int>(() => throw Generic));
        var gone = new COMException("gone", ComErrors.RPC_E_DISCONNECTED);
        Assert.Same(gone, Assert.Throws<COMException>(() => ComSafe.Try<string>(() => throw gone)));
        Assert.Throws<InvalidOperationException>(() => ComSafe.Try<string>(() => throw new InvalidOperationException()));
        Assert.Throws<ArgumentNullException>(() => ComSafe.Try<string>(null!));
    }

    private sealed class Html(string? value)
    {
        public int Calls;
        public string? Get() { Calls++; return value; }
    }

    private static AttachmentFacts Facts(int type = 1, bool hidden = false, string? cid = null, string? mime = null, string file = "a.bin") =>
        new(type, hidden, cid, mime, file);

    [Fact]
    public void IsInline_type_and_hidden_short_circuit()
    {
        var h = new Html("x");
        Assert.True(InlineAttachments.IsInline(Facts(type: 6), h.Get));
        Assert.True(InlineAttachments.IsInline(Facts(hidden: true), h.Get));
        Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData("<img src=\"cid:image001.png@01DA\">", "image001.png@01DA", "image/png", "image001.png", true, 1)]
    [InlineData("<img src=\"cid:image001.png@01DA\">", "<image001.png@01DA>", "image/png", "image001.png", true, 1)]
    [InlineData("CID:IMAGE001.PNG@01da'", "image001.png@01DA", "image/png", "image001.png", true, 1)]
    [InlineData("cid:image001.png@01DA2", "image001.png@01DA", "image/png", "image001.png", false, 1)]
    [InlineData("cid:abc", "", "image/png", "image001.png", false, 0)]
    [InlineData("cid:abc", "abc", "application/pdf", "report.pdf", false, 0)]
    [InlineData("cid:abc", "abc", null, "photo.JPG", true, 1)]
    [InlineData("cid:abc", "abc", "image/jpeg", "photo", true, 1)]
    [InlineData(null, "abc", "image/jpeg", "photo", false, 1)]
    public void IsInline_images(string? html, string cid, string? mime, string file, bool expected, int calls)
    {
        var h = new Html(html);
        Assert.Equal(expected, InlineAttachments.IsInline(Facts(cid: cid, mime: mime, file: file), h.Get));
        Assert.Equal(calls, h.Calls);
    }

    [Theory]
    [InlineData("x.png", null, true)]
    [InlineData("X.JPEG", null, true)]
    [InlineData("a.b.gif", null, true)]
    [InlineData(".svg", null, true)]
    [InlineData(null, "Image/GIF", true)]
    [InlineData("x.pdf", null, false)]
    [InlineData("png", null, false)]
    [InlineData("x.png.exe", null, false)]
    [InlineData("", null, false)]
    [InlineData(null, null, false)]
    public void IsImage(string? file, string? mime, bool expected) =>
        Assert.Equal(expected, InlineAttachments.IsImage(mime, file));

    [Theory]
    [InlineData("\"", true)]
    [InlineData("'", true)]
    [InlineData(")", true)]
    [InlineData(">", true)]
    [InlineData(" ", true)]
    [InlineData("&", true)]
    [InlineData("", true)]
    [InlineData("a", false)]
    [InlineData("7", false)]
    [InlineData(".", false)]
    [InlineData("@", false)]
    [InlineData("-", false)]
    [InlineData("_", false)]
    [InlineData("%", false)]
    [InlineData("+", false)]
    public void IsReferenced_boundaries(string after, bool expected) =>
        Assert.Equal(expected, InlineAttachments.IsReferenced("<img src=cid:abc" + after, "abc"));

    [Fact]
    public void IsReferenced_later_good_occurrence() =>
        Assert.True(InlineAttachments.IsReferenced("cid:abcd and cid:abc\"", "abc"));

    [Fact]
    public void FileNameFor_cases()
    {
        Assert.Equal("a.pdf", InlineAttachments.FileNameFor(" a.pdf ", "x", 1));
        Assert.Equal("Notes", InlineAttachments.FileNameFor(null, " Notes ", 2));
        Assert.Equal("attachment-3", InlineAttachments.FileNameFor("", "", 3));
        Assert.Equal("attachment-4", InlineAttachments.FileNameFor("  ", null, 4));
    }

    private static string? Boom() => throw new InvalidOperationException("must not be called");

    [Fact]
    public void ChooseSenderSmtp_cases()
    {
        Assert.Equal("a@x.org", MailAddresses.ChooseSenderSmtp("EX", "/o=x", () => "a@x.org", Boom));
        Assert.Equal("b@x.org", MailAddresses.ChooseSenderSmtp("ex", "/o=x", () => null, () => "b@x.org"));
        Assert.Null(MailAddresses.ChooseSenderSmtp("EX", null, () => "", () => null));
        Assert.Equal("c@x.org", MailAddresses.ChooseSenderSmtp("SMTP", " c@x.org ", Boom, Boom));
        Assert.Equal("d@x.org", MailAddresses.ChooseSenderSmtp("SMTP", "/o=ExchangeLabs/ou=x/cn=y", Boom, () => "d@x.org"));
        Assert.Equal("e@x.org", MailAddresses.ChooseSenderSmtp(null, "e@x.org", Boom, Boom));
    }

    [Fact]
    public void ChooseRecipientSmtp_cases()
    {
        Assert.Equal("p@x", MailAddresses.ChooseRecipientSmtp(() => "p@x", "a@x", Boom));
        Assert.Equal("a@x", MailAddresses.ChooseRecipientSmtp(() => null, "a@x", Boom));
        Assert.Equal("e@x", MailAddresses.ChooseRecipientSmtp(() => null, "/o=...", () => "e@x"));
        Assert.Null(MailAddresses.ChooseRecipientSmtp(() => "bad", "/o=", () => ""));
    }

    [Theory]
    [InlineData("a@b", true)]
    [InlineData(" a@b.org ", true)]
    [InlineData("a@", false)]
    [InlineData("@b", false)]
    [InlineData("a@@b", false)]
    [InlineData("a@b@c", false)]
    [InlineData("a b@c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("/o=x/cn=y", false)]
    public void LooksLikeSmtp(string? s, bool expected) => Assert.Equal(expected, MailAddresses.LooksLikeSmtp(s));

    [Fact]
    public void FormatList_cases()
    {
        var text = MailAddresses.FormatList([
            ("Alice", "alice@x.org"), ("", "bob@x.org"), ("Carol", null), ("dave@x.org", "DAVE@x.org"), (null, null), (" ", "/o=x"),
        ]);
        Assert.Equal("Alice <alice@x.org>; bob@x.org; Carol; DAVE@x.org", text);
        Assert.Null(MailAddresses.FormatList([]));
        Assert.Null(MailAddresses.FormatList([(null, null), (" ", " ")]));
        var many = MailAddresses.FormatList(Enumerable.Range(0, 600).Select(i => ((string?)"", (string?)$"u{i}@x.org")))!;
        var parts = many.Split("; ");
        Assert.Equal(500, parts.Length);
        Assert.Equal("u0@x.org", parts[0]);
        Assert.Equal("u499@x.org", parts[^1]);
    }

    [Fact]
    public void ItemNotFoundException_message()
    {
        var e = new ItemNotFoundException("ABC");
        Assert.Equal("Email \"ABC\" was not found in Outlook. It may have been deleted or moved to a folder that isn't indexed. Run search_emails again to get a current id.", e.Message);
        Assert.Equal("ABC", e.Id);
        var longId = new string('a', 199) + "bZ" + new string('c', 98);
        var l = new ItemNotFoundException(longId);
        Assert.Contains(longId[..200] + "…", l.Message);
        Assert.DoesNotContain(longId[..201], l.Message);
        Assert.Equal("X", new ItemNotFoundException(" X ").Id);
        Assert.Equal("", new ItemNotFoundException(null).Id);
        Assert.IsAssignableFrom<McpException>(e);
    }

    // ---- Resolver ----

    private sealed class FakeSource : IMailItemSource<object>
    {
        public readonly Dictionary<string, string?> StoreIds = [];
        public readonly Dictionary<(string, string), object> Items = [];
        public readonly Dictionary<(string, string, string), string> Hits = [];
        public readonly List<string> Calls = [];
        public Exception? ThrowOnGet;

        public string? StoreIdFor(string mailbox)
        {
            Calls.Add($"Store({mailbox})");
            return StoreIds.GetValueOrDefault(mailbox);
        }

        public object? TryGetById(string entryId, string storeId)
        {
            Calls.Add($"Get({entryId},{storeId})");
            if (ThrowOnGet is not null) throw ThrowOnGet;
            return Items.GetValueOrDefault((entryId, storeId));
        }

        public string? FindByMessageId(string mailbox, string folderPath, string internetMessageId)
        {
            Calls.Add($"Find({folderPath})");
            return Hits.GetValueOrDefault((mailbox, folderPath, internetMessageId));
        }
    }

    private static LocatedEmail Located(string mailbox, string id, string? storeId, string? imid) =>
        new(mailbox, new EmailDetail
        {
            RowId = 1, EntryId = id, StoreId = storeId, InternetMessageId = imid, Folder = "Inbox",
            EventTime = DateTimeOffset.UnixEpoch, Unread = false, HasAttachments = false, Attachments = [],
        });

    private static ResolvedMailItem<object> Run(FakeSource src, string? id, LocatedEmail? located,
        string[]? mailboxes = null, string[]? folders = null) =>
        MailItemResolver.Resolve<object>(id, _ => located, mailboxes ?? [""], _ => folders ?? ["Inbox", "Sent Items"], src);

    [Fact]
    public void Resolver_located_direct()
    {
        var src = new FakeSource();
        var item = new object();
        src.Items[("id", "S1")] = item;
        var r = Run(src, "id", Located("team@x.org", "id", "S1", "<m@x>"));
        Assert.Same(item, r.Item);
        Assert.Equal("team@x.org", r.Mailbox);
        Assert.Equal("id", r.EntryId);
        Assert.Equal("S1", r.StoreId);
        Assert.Equal(["Get(id,S1)"], src.Calls);
    }

    [Fact]
    public void Resolver_located_falls_back_to_message_id()
    {
        var src = new FakeSource();
        var item = new object();
        src.Hits[("", "Sent Items", "<m@x>")] = "NEW";
        src.Items[("NEW", "S1")] = item;
        var r = Run(src, "id", Located("", "id", "S1", "<m@x>"));
        Assert.Equal("NEW", r.EntryId);
        Assert.Same(item, r.Item);
        Assert.Equal(["Get(id,S1)", "Find(Inbox)", "Find(Sent Items)", "Get(NEW,S1)"], src.Calls);
    }

    [Fact]
    public void Resolver_hit_without_item_continues_then_throws()
    {
        var src = new FakeSource();
        src.Hits[("", "Inbox", "<m@x>")] = "N1";
        src.Hits[("", "Sent Items", "<m@x>")] = "N2";
        Assert.Throws<ItemNotFoundException>(() => Run(src, "id", Located("", "id", "S1", "<m@x>")));
        Assert.Equal(["Get(id,S1)", "Find(Inbox)", "Get(N1,S1)", "Find(Sent Items)", "Get(N2,S1)"], src.Calls);
    }

    [Fact]
    public void Resolver_no_imid_no_find()
    {
        var src = new FakeSource();
        Assert.Throws<ItemNotFoundException>(() => Run(src, "id", Located("", "id", "S1", null)));
        Assert.DoesNotContain(src.Calls, c => c.StartsWith("Find"));
    }

    [Fact]
    public void Resolver_store_id_fallback()
    {
        var src = new FakeSource();
        src.StoreIds["team@x.org"] = "S9";
        var item = new object();
        src.Items[("id", "S9")] = item;
        Assert.Same(item, Run(src, "id", Located("team@x.org", "id", null, null)).Item);

        var none = new FakeSource();
        Assert.Throws<ItemNotFoundException>(() => Run(none, "id", Located("team@x.org", "id", null, "<m@x>")));
        Assert.DoesNotContain(none.Calls, c => c.StartsWith("Get"));
    }

    [Fact]
    public void Resolver_not_located_scans_mailboxes()
    {
        var src = new FakeSource();
        src.StoreIds[""] = "S0";
        src.StoreIds["gone@x.org"] = null;
        src.StoreIds["team@x.org"] = "S1";
        src.Items[("id", "S1")] = new object();
        var r = Run(src, "id", null, ["", "gone@x.org", "team@x.org"]);
        Assert.Equal("team@x.org", r.Mailbox);
        Assert.Equal("S1", r.StoreId);
        Assert.DoesNotContain("Get(id,null)", src.Calls);
    }

    [Fact]
    public void Resolver_not_located_nowhere()
    {
        var src = new FakeSource();
        src.StoreIds[""] = "S0";
        Assert.Throws<ItemNotFoundException>(() => Run(src, "id", null));
        Assert.DoesNotContain(src.Calls, c => c.StartsWith("Find"));
    }

    [Fact]
    public void Resolver_trims_and_rejects_blank()
    {
        var src = new FakeSource();
        src.StoreIds[""] = "S0";
        src.Items[("id", "S0")] = new object();
        Assert.Equal("id", Run(src, " id ", null).EntryId);

        foreach (var blank in new string?[] { null, "" })
        {
            var s2 = new FakeSource();
            var locates = 0;
            Assert.Throws<ItemNotFoundException>(() =>
                MailItemResolver.Resolve<object>(blank, _ => { locates++; return null; }, [""], _ => [], s2));
            Assert.Empty(s2.Calls);
            Assert.Equal(0, locates);
        }
    }

    [Fact]
    public void Resolver_gone_propagates()
    {
        var src = new FakeSource { ThrowOnGet = new COMException("gone", ComErrors.RPC_E_DISCONNECTED) };
        Assert.Same(src.ThrowOnGet, Assert.Throws<COMException>(() => Run(src, "id", Located("", "id", "S1", null))));
    }

    [Fact]
    public void Resolver_null_arguments()
    {
        var src = new FakeSource();
        Assert.Throws<ArgumentNullException>(() => MailItemResolver.Resolve<object>("a", null!, [""], _ => [], src));
        Assert.Throws<ArgumentNullException>(() => MailItemResolver.Resolve<object>("a", _ => null, null!, _ => [], src));
        Assert.Throws<ArgumentNullException>(() => MailItemResolver.Resolve<object>("a", _ => null, [""], null!, src));
        Assert.Throws<ArgumentNullException>(() => MailItemResolver.Resolve<object>("a", _ => null, [""], _ => [], null!));
    }
}
