namespace ClippysRevenge.Core.Tests;

public class DraftRulesTests
{
    private const string Me = "me@example.org";

    private static DraftCandidate C(string? sender = "ann@contoso.example", string? to = "Me <me@example.org>", string? cc = null,
        string? subject = "Budget") =>
        new("E1", null, "<i@x>", "Budget", DateTimeOffset.UnixEpoch, subject, "Ann", sender, to, cc);

    private static string? Eval(DraftCandidate c, AiDraftingSettings? s = null, Func<bool>? replied = null, string mailbox = Me) =>
        DraftRules.EvaluateIndex(c, mailbox, s ?? new AiDraftingSettings(), replied ?? (() => false));

    [Fact]
    public void Plain_mail_matches_nothing() => Assert.Null(Eval(C()));

    [Fact]
    public void Self_is_case_insensitive() => Assert.Equal("self", Eval(C(sender: "ME@Example.org")));

    [Theory]
    [InlineData("noreply@x.org")]
    [InlineData("No-Reply+abc@x.org")]
    [InlineData("MAILER-DAEMON@x.org")]
    [InlineData("bounces@x.org")]
    [InlineData("do-not-reply@x.org")]
    public void NoReply_senders(string sender) => Assert.Equal("no_reply", Eval(C(sender: sender)));

    [Theory]
    [InlineData("noreplyx@x.org")]
    [InlineData("reply@x.org")]
    public void NoReply_lookalikes_pass(string sender) => Assert.Null(Eval(C(sender: sender)));

    [Theory]
    [InlineData("Automatic reply: hi")]
    [InlineData("  autoreply: x")]
    [InlineData("OUT OF OFFICE until Monday")]
    [InlineData("Undeliverable: Budget")]
    [InlineData("Delivery Status Notification (Failure)")]
    public void AutoSubjects(string subject) => Assert.Equal("auto_subject", Eval(C(subject: subject)));

    [Fact]
    public void Quoted_auto_subject_passes() => Assert.Null(Eval(C(subject: "Re: Automatic reply")));

    [Theory]
    [InlineData("contoso.example", "bob@sub.contoso.example", "excluded")]
    [InlineData("@contoso.example", "bob@sub.contoso.example", "excluded")]
    [InlineData("ann@contoso.example", "ann@contoso.example", "excluded")]
    [InlineData("notcontoso.example", "ann@contoso.example", null)]
    public void Exclude(string entry, string sender, string? expected) =>
        Assert.Equal(expected, Eval(C(sender: sender), new AiDraftingSettings { ExcludeSenders = [entry] }));

    [Fact]
    public void IncludeOnly()
    {
        var s = new AiDraftingSettings { IncludeOnlySenders = ["example.net"] };
        Assert.Equal("not_included", Eval(C(), s));
        Assert.Null(Eval(C(sender: "x@example.net"), s));
        Assert.Equal("not_included", Eval(C(sender: null), s));
    }

    [Fact]
    public void Unparseable_include_is_empty() =>
        Assert.Null(Eval(C(), new AiDraftingSettings { IncludeOnlySenders = ["bad entry"] }));

    [Fact]
    public void Direct_recipient()
    {
        Assert.Null(Eval(C(to: "Other <o@example.org>", cc: "me@EXAMPLE.org")));
        Assert.Equal("not_direct", Eval(C(to: "Other <o@example.org>", cc: "x@example.org")));
        Assert.Null(Eval(C(to: "Other <o@example.org>"), new AiDraftingSettings { RequireDirectRecipient = false }));
    }

    [Fact]
    public void Replied_in_index() => Assert.Equal("replied", Eval(C(), replied: () => true));

    [Fact]
    public void First_match_wins_and_replied_is_lazy()
    {
        Assert.Equal("self", Eval(C(sender: "noreply@example.org", subject: "Automatic reply:"), mailbox: "noreply@example.org"));
        var n = 0;
        Assert.Equal("no_reply", Eval(C(sender: "noreply@x.org"), replied: () => { n++; return true; }));
        Assert.Equal(0, n);
    }

    [Fact]
    public void EvaluateIndex_argument_checks()
    {
        Assert.Throws<ArgumentNullException>(() => DraftRules.EvaluateIndex(null!, Me, new AiDraftingSettings(), () => false));
        Assert.ThrowsAny<ArgumentException>(() => DraftRules.EvaluateIndex(C(), " ", new AiDraftingSettings(), () => false));
    }

    private static CandidateFacts F(string? cls = "IPM.Note", string? headers = null, int? verb = null, bool draft = false) =>
        new(cls, "<i@x>", "Budget", headers, verb, draft);

    [Fact]
    public void Com_gone() => Assert.Equal("gone", DraftRules.EvaluateCom(null));

    [Theory]
    [InlineData("IPM.Note", null)]
    [InlineData("ipm.note", null)]
    [InlineData("IPM.Note.SMIME", "not_note")]
    [InlineData("IPM.Schedule.Meeting.Request", "not_note")]
    [InlineData("REPORT.IPM.Note.NDR", "not_note")]
    [InlineData(null, "not_note")]
    public void Com_class(string? cls, string? expected) => Assert.Equal(expected, DraftRules.EvaluateCom(F(cls)));

    [Theory]
    [InlineData("List-Id: <x.example.org>\r\n", "list")]
    [InlineData("List-Unsubscribe: <mailto:u@x>", "list")]
    [InlineData("Precedence: Bulk", "list")]
    [InlineData("Precedence: first-class", null)]
    [InlineData("List-Id:\r\n <x>", "list")]
    [InlineData("Auto-Submitted: auto-replied", "auto")]
    [InlineData("Auto-Submitted: no", null)]
    [InlineData("Auto-Submitted: No ; comment", null)]
    [InlineData("Auto-Submitted:", "auto")]
    public void Com_headers(string headers, string? expected) =>
        Assert.Equal(expected, DraftRules.EvaluateCom(F(headers: headers)));

    [Theory]
    [InlineData(102, "replied")]
    [InlineData(103, "replied")]
    [InlineData(104, null)]
    public void Com_last_verb(int verb, string? expected) => Assert.Equal(expected, DraftRules.EvaluateCom(F(verb: verb)));

    [Fact]
    public void Com_draft_exists_and_order()
    {
        Assert.Equal("draft_exists", DraftRules.EvaluateCom(F(draft: true)));
        Assert.Equal("not_note", DraftRules.EvaluateCom(F(cls: "IPM.Schedule.Meeting.Request", draft: true)));
    }

    [Fact]
    public void BeforeCreate_only_rechecks_three()
    {
        Assert.Equal("gone", DraftRules.EvaluateBeforeCreate(null));
        Assert.Equal("replied", DraftRules.EvaluateBeforeCreate(F(verb: 102)));
        Assert.Equal("draft_exists", DraftRules.EvaluateBeforeCreate(F(draft: true)));
        Assert.Null(DraftRules.EvaluateBeforeCreate(F(headers: "List-Id: <x>")));
    }

    [Fact]
    public void Addresses_parse()
    {
        Assert.Equal(["a@x.org", "b@y.org", "c@z.org"], DraftRules.Addresses("A <a@x.org>; b@y.org; Name Only; <c@z.org>; "));
        Assert.Empty(DraftRules.Addresses(null));
    }
}
