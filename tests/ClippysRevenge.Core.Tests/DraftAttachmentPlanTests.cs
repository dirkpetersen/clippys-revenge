using ClippysRevenge.Core;
using Xunit;

namespace ClippysRevenge.Core.Tests;

/// <summary>Shared request builders for the attachment plan and collect tests.</summary>
internal static class AttFx
{
    public static readonly AiIdentity Me = new("Me", "me@contoso.com");
    public static readonly DateTimeOffset T0 = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public const string Share = "https://contoso.sharepoint.com/sites/x/Shared%20Documents/";

    public static readonly SyncedRoot Root =
        new("https://contoso.sharepoint.com/sites/x/Shared Documents/", @"C:\OD\X", "teamsite", false);

    public static AttachmentInfo A(int pos, string name, long? size = null, bool inline = false) =>
        new(pos, name, size, inline);

    public static ThreadAttachmentSource Th(string id, int minutesAgo, string? email = "me@contoso.com",
        string? body = null, AttachmentInfo[]? atts = null) =>
        new(id, T0.AddMinutes(-minutesAgo), "Sender", email, body, atts ?? []);

    public static AttachmentRequest Req(IReadOnlyList<AttachmentInfo>? anchor = null, string? body = null,
        string? subject = null, IReadOnlyList<ThreadAttachmentSource>? thread = null,
        string? senderEmail = "bob@contoso.com", string mailbox = "", TimeZoneInfo? zone = null,
        string anchorId = "A", WorkPriority priority = WorkPriority.Background) =>
        new(mailbox, anchorId, subject, body, "Bob", senderEmail, T0, anchor ?? [], thread ?? [], Me, null,
            priority, zone ?? TimeZoneInfo.Utc);
}

public class DraftAttachmentPlanTests
{
    private static IReadOnlyList<PlannedAttachment> Plan(AttachmentRequest r, params SyncedRoot[] roots) =>
        DraftAttachmentPlan.Build(r, roots);

    [Fact]
    public void Tiers_and_order()
    {
        var r = AttFx.Req(
            anchor: [AttFx.A(1, "a.pdf"), AttFx.A(2, "logo.png", inline: true)],
            body: "see x.xlsx\n" + AttFx.Share + "l.docx",
            thread:
            [
                AttFx.Th("T1", 60, atts: [AttFx.A(1, "x.xlsx")]),
                AttFx.Th("T2", 30, atts: [AttFx.A(1, "old.docx")]),
            ]);
        var p = Plan(r, AttFx.Root);
        Assert.Equal(["a.pdf", "l.docx", "x.xlsx", "old.docx"], p.Select(i => i.Name).ToArray());
        Assert.Equal([1, 1, 2, 3], p.Select(i => i.Tier).ToArray());
        Assert.DoesNotContain(p, i => i.Name == "logo.png");
        Assert.Equal([AttachmentOrigin.Email, AttachmentOrigin.Email, AttachmentOrigin.Thread, AttachmentOrigin.Thread],
            p.Select(i => i.Origin).ToArray());
    }

    [Fact]
    public void Mention_by_stem()
    {
        var r = AttFx.Req(body: "the quarterly plan is attached",
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "Quarterly Plan.docx")])]);
        Assert.Equal(2, Plan(r).Single().Tier);
    }

    [Theory]
    [InlineData("abc.pdf", "abc", 3)]
    [InlineData("abc.pdf", "abc.pdf", 2)]
    [InlineData("ab.c", "ab.c", 2)]
    [InlineData("plan.docx", "planning", 3)]
    [InlineData("plan.docx", "(plan.docx)", 2)]
    [InlineData("plan.docx", "xplan.docx", 3)]
    [InlineData("R\u00e9sum\u00e9.pdf", "r\u00e9sum\u00e92", 3)]
    [InlineData("R\u00e9sum\u00e9.pdf", "my r\u00e9sum\u00e9, thanks", 2)]
    public void Mention_rules(string name, string text, int tier)
    {
        var r = AttFx.Req(body: text, thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, name)])]);
        Assert.Equal(tier, Plan(r).Single().Tier);
    }

    [Fact]
    public void Subject_counts_and_quoted_history_does_not()
    {
        var thread = new[] { AttFx.Th("T1", 10, atts: [AttFx.A(1, "budget.xlsx")]) };
        Assert.Equal(2, Plan(AttFx.Req(subject: "Re: budget.xlsx", thread: thread)).Single().Tier);

        var quoted = AttFx.Req(body: "Thanks!\n\n-----Original Message-----\nFrom: x\nplease see budget.xlsx", thread: thread);
        Assert.Equal(3, Plan(quoted).Single().Tier);
    }

    [Fact]
    public void Dedupe_by_name_and_size()
    {
        var one = AttFx.Req(anchor: [AttFx.A(1, "r.pdf", 100)], thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "R.PDF ", 100)])]);
        var p = Plan(one).Single();
        Assert.Equal(AttachmentOrigin.Email, p.Origin);
        Assert.Equal(1, p.Tier);

        Assert.Equal(2, Plan(AttFx.Req(anchor: [AttFx.A(1, "r.pdf", 100)],
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "r.pdf", 200)])])).Count);
        Assert.Equal(2, Plan(AttFx.Req(anchor: [AttFx.A(1, "r.pdf")],
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "r.pdf")])])).Count);
        Assert.Equal(2, Plan(AttFx.Req(anchor: [AttFx.A(1, "r.pdf", 0)],
            thread: [AttFx.Th("T1", 10, atts: [AttFx.A(1, "r.pdf", 0)])])).Count);
    }

    [Fact]
    public void Dedupe_keeps_best_tier_then_newest()
    {
        // Two tier-3 copies: the newer message's stays.
        var r = AttFx.Req(body: "see report.pdf",
            thread:
            [
                AttFx.Th("NEW", 10, atts: [AttFx.A(1, "other.pdf", 7)]),
                AttFx.Th("OLD", 50, atts: [AttFx.A(1, "other.pdf", 7)]),
            ]);
        var two = Plan(r);
        Assert.Single(two);
        Assert.Equal("NEW", two[0].EntryId);

        var mixed = AttFx.Req(body: "see rep.pdf",
            thread:
            [
                AttFx.Th("N", 10, atts: [AttFx.A(1, "rep.pdf", 7)]),
                AttFx.Th("O", 50, atts: [AttFx.A(1, "rep.pdf", 7)]),
            ]);
        var kept = Plan(mixed);
        Assert.Single(kept);
        Assert.Equal("N", kept[0].EntryId);
    }

    [Fact]
    public void Links_dedupe_by_url_and_by_mapped_path()
    {
        var u = AttFx.Share + "a.docx";
        var r = AttFx.Req(body: u, thread: [AttFx.Th("T1", 10, body: u)]);
        Assert.Single(Plan(r, AttFx.Root));

        var share = "https://contoso.sharepoint.com/:w:/r/sites/x/Shared%20Documents/a.docx";
        var two = AttFx.Req(body: u, thread: [AttFx.Th("T1", 10, body: share)]);
        var p = Plan(two, AttFx.Root);
        Assert.Single(p);
        Assert.Equal(AttachmentOrigin.Email, p[0].Origin);
    }

    [Fact]
    public void Cap_counts_only_items_that_need_a_read()
    {
        var atts = Enumerable.Range(1, 25).Select(i => AttFx.A(i, $"f{i}.pdf")).ToList();
        var p = Plan(AttFx.Req(anchor: atts));
        Assert.Equal(25, p.Count);
        Assert.All(p.Take(20), i => Assert.Null(i.SkipReason));
        Assert.All(p.Skip(20), i => Assert.Equal(AttachmentReasons.TooManyFiles, i.SkipReason));

        var withImages = atts.Concat(Enumerable.Range(26, 5).Select(i => AttFx.A(i, $"p{i}.png"))).ToList();
        var q = Plan(AttFx.Req(anchor: withImages));
        Assert.Equal(20, q.Count(i => i.SkipReason is null));
        Assert.Equal(5, q.Count(i => i.SkipReason == AttachmentReasons.Image));
        Assert.Equal(5, q.Count(i => i.SkipReason == AttachmentReasons.TooManyFiles));
    }

    [Fact]
    public void Format_preskips()
    {
        var p = Plan(AttFx.Req(anchor:
        [
            AttFx.A(1, "a.zip"), AttFx.A(2, "noext"), AttFx.A(3, "x.png"), AttFx.A(4, "Fwd.msg"),
        ]));
        Assert.Equal("Clippy can't read .zip files", p[0].SkipReason);
        Assert.Null(p[1].SkipReason);   // no extension: the COM read knows the real type (older embedded-item rows)
        Assert.Equal(AttachmentReasons.Image, p[2].SkipReason);
        Assert.Null(p[3].SkipReason);
        Assert.All(p, i => Assert.Equal(AttachmentKind.File, i.Kind));
    }

    [Theory]
    [InlineData("attachment-3", AttachmentReasons.UnknownKind)]
    [InlineData("attachment-12", AttachmentReasons.UnknownKind)]
    [InlineData("attachment-", null)]
    [InlineData("attachment-x", null)]
    [InlineData("my attachment-3", null)]
    [InlineData("Quarterly review", null)]
    public void Extensionless_name_is_read_unless_it_is_the_fallback_name(string name, string? skip)
    {
        var p = Assert.Single(Plan(AttFx.Req(anchor: [AttFx.A(1, name)])));
        Assert.Equal(skip, p.SkipReason);
    }

    [Theory]
    [InlineData("me@x.com", false)]
    [InlineData("a@hotmail.co.uk", true)]
    [InlineData("a@hotmail.fr", true)]
    [InlineData("a@hotmail.de", true)]
    [InlineData("a@outlook.de", true)]
    [InlineData("a@outlook.fr", true)]
    [InlineData("a@outlook.jp", true)]
    [InlineData("a@live.co.uk", true)]
    [InlineData("a@live.nl", true)]
    [InlineData("a@yahoo.co.uk", true)]
    [InlineData("a@yahoo.co.jp", true)]
    [InlineData("a@yahoo.com.br", true)]
    [InlineData("a@yahoo.it", true)]
    [InlineData("a@ymail.com", true)]
    [InlineData("a@rocketmail.com", true)]
    [InlineData("a@mail.com", true)]
    [InlineData("a@t-online.de", true)]
    [InlineData("a@qq.com", true)]
    [InlineData("a@163.com", true)]
    [InlineData("a@126.com", true)]
    [InlineData("a@yandex.ru", true)]
    [InlineData("a@yandex.com", true)]
    [InlineData("a@mail.ru", true)]
    [InlineData("a@comcast.net", true)]
    [InlineData("a@verizon.net", true)]
    [InlineData("a@att.net", true)]
    [InlineData("a@fastmail.com", true)]
    [InlineData("a@zoho.com", true)]
    [InlineData("a@gmx.at", true)]
    [InlineData("a@outlook.contoso.com", false)]
    [InlineData("a@live.contoso.com", false)]
    [InlineData("a@yahoo.example.org", false)]
    [InlineData("a@outlook.company", false)]
    [InlineData("a@hotmailer.fr", false)]
    [InlineData("a@contoso.com", false)]
    public void Consumer_domain_rule_blocks_a_colleague_match(string own, bool consumer)
    {
        var colleague = "bob@" + own[(own.IndexOf('@') + 1)..];
        // A consumer domain is not the organization: only the own address counts. Otherwise the same domain matches.
        Assert.True(DraftAttachmentPlan.MayOpenShared(own, new AiIdentity("Me", own), ""));
        Assert.Equal(!consumer, DraftAttachmentPlan.MayOpenShared(colleague, new AiIdentity("Me", own), ""));
    }

    [Theory]
    [InlineData("me@contoso.com", "", true)]
    [InlineData("ME@Contoso.com ", "", true)]
    [InlineData("bob@contoso.com", "", true)]
    [InlineData("bob@eu.contoso.com", "", true)]
    [InlineData("bob@contoso.com.evil.example", "", false)]
    [InlineData("bob@evilcontoso.com", "", false)]
    [InlineData("bob@com", "", false)]
    [InlineData("bob@other.org", "", false)]
    [InlineData("team@other.org", "team@other.org", true)]
    [InlineData("/o=ExchangeLabs/ou=x/cn=y", "", false)]
    [InlineData(null, "", false)]
    public void Sender_rule(string? sender, string mailbox, bool expected) =>
        Assert.Equal(expected, DraftAttachmentPlan.MayOpenShared(sender, AttFx.Me, mailbox));

    [Fact]
    public void Sender_rule_without_own_smtp_matches_only_the_mailbox()
    {
        var none = new AiIdentity("Me", "");
        Assert.False(DraftAttachmentPlan.MayOpenShared("bob@contoso.com", none, ""));
        Assert.True(DraftAttachmentPlan.MayOpenShared("team@other.org", none, "team@other.org"));
    }

    [Fact]
    public void Host_and_sender_rules_together()
    {
        var body = "https://www.dropbox.com/s/abc/a.docx\n" + AttFx.Share + "b.docx";
        var internalPlan = Plan(AttFx.Req(body: body), AttFx.Root);
        var only = Assert.Single(internalPlan);
        Assert.Equal("b.docx", only.Name);
        Assert.Null(only.SkipReason);

        var external = Plan(AttFx.Req(body: AttFx.Share + "b.docx", senderEmail: "eve@other.org",
            anchor: [AttFx.A(1, "report.pdf")]), AttFx.Root);
        Assert.Null(external.Single(i => i.Name == "report.pdf").SkipReason);
        var link = external.Single(i => i.Name == "b.docx");
        Assert.Equal(AttachmentReasons.FromOutside, link.SkipReason);
        Assert.Null(link.Mapped);
    }

    [Theory]
    [InlineData("me@gmail.com", true)]
    [InlineData("ME@gmail.com", true)]
    [InlineData("bob@gmail.com", false)]
    [InlineData("bob@outlook.com", false)]
    public void Freemail_domain_is_not_the_organization(string sender, bool expected) =>
        Assert.Equal(expected, DraftAttachmentPlan.MayOpenShared(sender, new AiIdentity("Me", "me@gmail.com"), ""));

    [Theory]
    [InlineData("me@outlook.com", "x@outlook.com")]
    [InlineData("me@hotmail.com", "x@hotmail.com")]
    [InlineData("me@live.com", "x@live.com")]
    [InlineData("me@msn.com", "x@msn.com")]
    [InlineData("me@googlemail.com", "x@googlemail.com")]
    [InlineData("me@yahoo.com", "x@yahoo.com")]
    [InlineData("me@icloud.com", "x@icloud.com")]
    [InlineData("me@me.com", "x@me.com")]
    [InlineData("me@aol.com", "x@aol.com")]
    [InlineData("me@proton.me", "x@proton.me")]
    [InlineData("me@protonmail.com", "x@protonmail.com")]
    [InlineData("me@gmx.net", "x@gmx.net")]
    [InlineData("me@web.de", "x@web.de")]
    public void Freemail_list_only_allows_own_address(string own, string other)
    {
        var id = new AiIdentity("Me", own);
        Assert.True(DraftAttachmentPlan.MayOpenShared(own, id, ""));
        Assert.False(DraftAttachmentPlan.MayOpenShared(other, id, ""));
    }

    [Fact]
    public void Freemail_user_with_shared_mailbox_still_matches_the_mailbox() =>
        Assert.True(DraftAttachmentPlan.MayOpenShared("team@gmail.com", new AiIdentity("Me", "me@gmail.com"), "team@gmail.com"));

    [Fact]
    public void Link_in_quoted_history_of_the_users_sent_row_is_not_the_users()
    {
        var quoted = "Thanks, will look.\n\n-----Original Message-----\nFrom: eve@other.org\n" + AttFx.Share + "a.docx";
        var r = AttFx.Req(thread: [AttFx.Th("S1", 10, email: "me@contoso.com", body: quoted)]);
        Assert.Empty(Plan(r, AttFx.Root));
    }

    [Fact]
    public void Link_only_in_the_forwarded_part_of_a_colleagues_mail_gives_no_item()
    {
        var fwd = "FYI\n\n-----Original Message-----\nFrom: eve@other.org\n" + AttFx.Share + "a.docx";
        Assert.Empty(Plan(AttFx.Req(body: fwd, senderEmail: "bob@contoso.com"), AttFx.Root));
        Assert.Empty(Plan(AttFx.Req(thread: [AttFx.Th("T1", 10, email: "bob@contoso.com", body: fwd)]), AttFx.Root));
    }

    [Fact]
    public void Link_in_the_users_own_new_text_still_counts()
    {
        var body = "See " + AttFx.Share + "a.docx\n\n-----Original Message-----\nFrom: eve@other.org\nold";
        var anchor = Assert.Single(Plan(AttFx.Req(body: body), AttFx.Root));
        Assert.Equal("a.docx", anchor.Name);
        var thread = Assert.Single(Plan(AttFx.Req(thread: [AttFx.Th("S1", 10, body: body)]), AttFx.Root));
        Assert.Equal("a.docx", thread.Name);
        Assert.Null(thread.SkipReason);
    }

    [Fact]
    public void Link_on_a_quoted_line_of_an_inline_reply_is_not_the_senders()
    {
        // An inline reply: the quote is followed by own text, so MailText.OwnText keeps the ">" lines.
        var inline = "Quoting you:\n> see " + AttFx.Share + "a.docx\nMy answer is no.";
        Assert.Empty(Plan(AttFx.Req(body: inline), AttFx.Root));
        Assert.Empty(Plan(AttFx.Req(thread: [AttFx.Th("S1", 10, body: inline)]), AttFx.Root));

        var withOwn = inline + "\nMine: " + AttFx.Share + "b.docx";
        Assert.Equal("b.docx", Assert.Single(Plan(AttFx.Req(body: withOwn), AttFx.Root)).Name);
        Assert.Equal("b.docx", Assert.Single(Plan(AttFx.Req(thread: [AttFx.Th("S1", 10, body: withOwn)]), AttFx.Root)).Name);
    }

    [Fact]
    public void Link_mapping()
    {
        var p = Plan(AttFx.Req(body: AttFx.Share + "b.docx"), AttFx.Root).Single();
        Assert.IsType<MappedLink.Local>(p.Mapped);

        var token = Plan(AttFx.Req(body: "https://contoso.sharepoint.com/:w:/s/x/EaBcDeF?e=x"), AttFx.Root).Single();
        Assert.Equal(AttachmentReasons.NotSynced, token.SkipReason);
    }

    [Fact]
    public void Thread_row_of_the_anchor_is_ignored() =>
        Assert.Empty(Plan(AttFx.Req(thread: [AttFx.Th("A", 5, atts: [AttFx.A(1, "a.pdf")])])));

    [Fact]
    public void Empty_request_gives_nothing() => Assert.Empty(Plan(AttFx.Req()));

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DraftAttachmentPlan.Build(null!, []));
        Assert.Throws<ArgumentNullException>(() => DraftAttachmentPlan.Build(AttFx.Req(), null!));
    }
}
