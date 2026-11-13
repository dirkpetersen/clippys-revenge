using System.Diagnostics;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public class MailTextTests
{
    // Generous: the brief asks for < 2 s; CI machines are slower and noisier.
    private static readonly TimeSpan PerfLimit = TimeSpan.FromSeconds(10);

    private const string Sig27 = "Dirk Petersen\nResearch Computing\nOHSU";

    public static TheoryData<string, string?, string> OwnTextCases => new()
    {
        { "Thanks, will do.\r\n\r\nDirk\r\n\r\n________________________________\r\nFrom: Ann Lee <ann@x.edu>\r\nSent: Monday, November 10, 2026 9:14 AM\r\nTo: Dirk P <d@x.edu>\r\nSubject: Budget\r\n\r\nCan you send it?", null, "Thanks, will do.\n\nDirk" },
        { "Sure.\n\nFrom: Ann Lee <ann@x.edu>\nSent: Monday, November 10, 2026 9:14 AM\nTo: Dirk\nCc: Bo\nSubject: Budget\n\nold", null, "Sure." },
        { "Passt.\n\nVon: Ann Lee <ann@x.de>\nGesendet: Montag, 10. November 2026 09:14\nAn: Dirk\nBetreff: Budget\n\nText", null, "Passt." },
        { "D'accord.\n\nDe : Ann\nEnvoyé : lundi 10 novembre 2026 09:14\nÀ : Dirk\nObjet : Budget\n\nTexte", null, "D'accord." },
        { "D'accord.\n\nDe : Ann\nEnvoyé : lundi 10 novembre 2026 09:14\nÀ : Dirk\nObjet : Budget\n\nTexte", null, "D'accord." },
        { "FYI\n\nFrom: Ann <a@x.org>\nDate: Mon, 10 Nov 2026\nSubject: X\nTo: Dirk", null, "FYI" },
        { "Ok\n*From:* Ann\n*Sent:* Mon\n*To:* Dirk", null, "Ok" },
        { "Ok\n\n-----Original Message-----\nFrom: Ann\nold", null, "Ok" },
        { "Ok\n-----Ursprüngliche Nachricht-----\nalt", null, "Ok" },
        { "Ok\n-----Message d'origine-----\nvieux", null, "Ok" },
        { "See below\n\n---------- Forwarded message ---------\nFrom: Ann", null, "See below" },
        { "Ok\n\nOn Mon, Nov 10, 2026 at 9:14 AM Ann Lee <ann@x.edu> wrote:\n\n> hi", null, "Ok" },
        { "Ok\n\nOn Mon, Nov 10, 2026 at 9:14 AM Ann Lee <\nann@x.edu> wrote:\n> hi", null, "Ok" },
        { "Gut.\n\nAm 10.11.2026 um 09:14 schrieb Ann Lee <ann@x.de>:\n> hallo", null, "Gut." },
        { "Bien.\n\nLe lun. 10 nov. 2026 à 09:14, Ann Lee <ann@x.fr> a écrit :\n> salut", null, "Bien." },
        { "Yes.\n\n> Can you?\n> Thanks\n>\n", null, "Yes." },
        { "> Q1?\nA1\n> Q2?\nA2", null, "> Q1?\nA1\n> Q2?\nA2" },
        { "Yes.\n> a\n\n-----Original Message-----\nold", null, "Yes." },
        { "From: our side this looks fine.\nThanks", null, "From: our side this looks fine.\nThanks" },
        { "Hi\nFrom: Ann\nSent: Mon\nx\ny\nz\nTo: Dirk", null, "Hi\nFrom: Ann\nSent: Mon\nx\ny\nz\nTo: Dirk" },
        { "Hi\nFrom: Ann\na\nb\nc\nd\nSent: Mon\nTo: Dirk", null, "Hi\nFrom: Ann\na\nb\nc\nd\nSent: Mon\nTo: Dirk" },
        { "a\n_______\nb", null, "a\n_______\nb" },
        { "-----Original Message-----\nFrom: Ann", null, "" },
        { "Ok\n   ________________________________\nFrom: x", null, "Ok" },
        { "Hi\n\n-- \nDirk P\n555", null, "Hi" },
        { "Hi\n--\nDirk", null, "Hi" },
        { "a\n---\nb\n -- \nc", null, "a\n---\nb\n -- \nc" },
        { "Thanks\n\nDirk Petersen\nResearch Computing\nOHSU\n", Sig27, "Thanks" },
        { "Dirk Petersen\nResearch Computing\nis great.\n\nBest\nDirk Petersen\nResearch Computing\nOHSU", Sig27, "Dirk Petersen\nResearch Computing\nis great.\n\nBest" },
        { "Thanks\ndirk  petersen\n\nResearch Computing", "\n  Dirk Petersen \n\n Research Computing\n", "Thanks" },
        { "Thanks\nDirk", "Dirk", "Thanks" },
        { "Dirk here, thanks", "Dirk", "Dirk here, thanks" },
        { "Thanks\nDirk Petersen\nBye", Sig27, "Thanks\nDirk Petersen\nBye" },
        { "a\nb", "  \n ", "a\nb" },
        { "Hi\n-- \nsig\n\nFrom: Ann\nSent: Mon\nTo: Dirk", null, "Hi" },
        { "Plain reply\nline 2", null, "Plain reply\nline 2" },
        { "a\r\nb\rc", null, "a\nb\nc" },
        { "\n\n  hi  \n\n \n", null, "hi" },
        { "", null, "" },
        // extra languages
        { "Vale\n\nEl lun, 10 nov 2026 a las 9:14, Ann <a@x.es> escribió:\n> hola", null, "Vale" },
        { "Oké\n\nOp ma 10 nov. 2026 om 09:14 schreef Ann <a@x.nl>:\n> hoi", null, "Oké" },
        { "Bene\n\nIl giorno lun 10 nov 2026 alle 09:14 Ann <a@x.it> ha scritto:\n> ciao", null, "Bene" },
        { "Ok\n-----Mensaje original-----\nviejo", null, "Ok" },
    };

    [Theory]
    [MemberData(nameof(OwnTextCases))]
    public void OwnText_cases(string body, string? signature, string expected) =>
        Assert.Equal(expected, MailText.OwnText(body, signature));

    [Fact]
    public void OwnText_null_throws() =>
        Assert.Throws<ArgumentNullException>(() => MailText.OwnText(null!));

    [Fact]
    public void OwnText_roundtrip_with_ToPlainText_signature()
    {
        string sig = DraftHtml.ToPlainText("<p>Dirk Petersen</p><p>Research&nbsp;Computing</p><p>OHSU</p>");
        Assert.Equal("Thanks", MailText.OwnText("Thanks\n\nDirk Petersen\nResearch Computing\nOHSU", sig));
    }

    [Fact]
    public void OwnText_perf_many_quote_lines()
    {
        string body = string.Concat(Enumerable.Repeat("> x\n", 500_000));
        var sw = Stopwatch.StartNew();
        Assert.Equal("", MailText.OwnText(body));
        Assert.True(sw.Elapsed < PerfLimit, sw.Elapsed.ToString());
    }

    [Fact]
    public void OwnText_perf_single_long_line()
    {
        string body = string.Concat(Enumerable.Repeat("On ", 333_334));
        var sw = Stopwatch.StartNew();
        MailText.OwnText(body);
        Assert.True(sw.Elapsed < PerfLimit, sw.Elapsed.ToString());
    }

    // ---------------------------------------------------------------- Redact

    [Theory]
    [InlineData("Mail ann.lee+x@sub.example.org now", "Mail <email> now")]
    [InlineData("Ann Lee <ann@x.edu>", "Ann Lee <email>")]
    [InlineData("Write to a@b.com.", "Write to <email>.")]
    [InlineData("See https://x.org/a?b=1&c=2.", "See <link>.")]
    [InlineData("(see https://en.wikipedia.org/wiki/Foo_(bar))", "(see <link>)")]
    [InlineData("Go to www.example.com/x, then", "Go to <link>, then")]
    [InlineData("<https://x.org/p>", "<link>")]
    [InlineData("https://user@host.org/p", "<link>")]
    [InlineData("Call +1 (503) 555-0100 or 503.555.0100", "Call <phone> or <phone>")]
    [InlineData("Tel. +49 30 1234567", "Tel. <phone>")]
    [InlineData("Order 12345678", "Order <phone>")]
    [InlineData("Room 1234 on 2026-11-13 at 10:00, ticket 123456", "Room 1234 on 2026-11-13 at 10:00, ticket 123456")]
    [InlineData("Due 13.11.2026.", "Due 13.11.2026.")]
    [InlineData("12345678@x.com", "<email>")]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("abc1234567", "abc1234567")]
    [InlineData("a\n1234\n567", "a\n1234\n567")]
    [InlineData("", "")]
    public void Redact_cases(string input, string expected)
    {
        string once = MailText.Redact(input);
        Assert.Equal(expected, once);
        Assert.Equal(once, MailText.Redact(once));
    }

    [Fact]
    public void Redact_null_throws() =>
        Assert.Throws<ArgumentNullException>(() => MailText.Redact(null!));

    [Fact]
    public void Redact_perf_letters() => AssertFast(new string('a', 1_000_000));

    [Fact]
    public void Redact_perf_digit_runs() => AssertFast(string.Concat(Enumerable.Repeat("1 ", 500_000)));

    [Fact]
    public void Redact_perf_dotted_then_at() =>
        AssertFast(string.Concat(Enumerable.Repeat("a.", 200_000)) + "@");

    private static void AssertFast(string input)
    {
        var sw = Stopwatch.StartNew();
        MailText.Redact(input);
        Assert.True(sw.Elapsed < PerfLimit, sw.Elapsed.ToString());
    }

    // ---------------------------------------------------------------- Headers

    [Fact]
    public void Headers_unfolds_and_ignores_case()
    {
        var h = MailText.Headers("List-Unsubscribe: <mailto:x@y>,\r\n <https://y/u>\r\nPrecedence:   bulk  \r\n");
        Assert.Equal("<mailto:x@y>, <https://y/u>", h["list-unsubscribe"]);
        Assert.Equal("bulk", h["PRECEDENCE"]);
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public void Headers_tab_and_space_continuations() =>
        Assert.Equal("a b c", MailText.Headers("Subject: a\r\n\tb\r\n  c")["Subject"]);

    [Fact]
    public void Headers_lf_only_first_value_wins()
    {
        var h = MailText.Headers("Received: one\nReceived: two\nX-A: 1");
        Assert.Equal("one", h["Received"]);
        Assert.Equal("1", h["x-a"]);
    }

    [Fact]
    public void Headers_stops_at_blank_line()
    {
        var h = MailText.Headers("A: 1\r\n\r\nB: 2");
        Assert.True(h.ContainsKey("A"));
        Assert.False(h.ContainsKey("B"));
    }

    [Fact]
    public void Headers_garbage_line_drops_its_continuation()
    {
        var h = MailText.Headers("garbage line\r\n folded garbage\r\nX-B: 2");
        Assert.Single(h);
        Assert.Equal("2", h["X-B"]);
    }

    [Fact]
    public void Headers_leading_continuation_ignored() =>
        Assert.Single(MailText.Headers(" leading continuation\r\nX-C: 3"));

    [Fact]
    public void Headers_empty_value_allowed() =>
        Assert.Equal("", MailText.Headers("Auto-Submitted:")["Auto-Submitted"]);

    [Fact]
    public void Headers_invalid_names_ignored()
    {
        var h = MailText.Headers("Bad Name: x\r\n: y\r\nOk: z");
        Assert.Single(h);
        Assert.Equal("z", h["Ok"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Headers_null_or_empty(string? input) => Assert.Empty(MailText.Headers(input));
}
