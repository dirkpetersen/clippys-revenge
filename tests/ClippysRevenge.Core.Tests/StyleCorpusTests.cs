using System.Globalization;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class StyleCorpusTests
{
    private static readonly DateTimeOffset Now = ManualTimeProvider.T0;
    private const string Own = "dirk@example.org";

    private static SentMail S(string id, double daysAgo, string? to, string? body, string? subject = "Hi", string? cc = null) =>
        new(id, null, Now.AddDays(-daysAgo), subject, to, cc, body);

    /// <summary>word repeated with spaces to exactly n chars; the last char is never a space.</summary>
    private static string Body(int n, string word = "word")
    {
        var sb = new System.Text.StringBuilder();
        while (sb.Length < n) sb.Append(word).Append(' ');
        var s = sb.ToString()[..n];
        return s.EndsWith(' ') ? s[..^1] + word[^1] : s;
    }

    /// <summary>Adds count filler rows (internal-short, distinct recipients, old).</summary>
    private static List<SentMail> Fill(int count, List<SentMail>? into = null)
    {
        var l = into ?? [];
        for (int i = 0; i < count; i++)
            l.Add(S("f" + i, 100 + i, $"f{i}@example.org", "FILL " + Body(40, "fill")));
        return l;
    }

    private static StyleCorpusResult.Built Built(IReadOnlyList<SentMail> rows, int seed = 1, IReadOnlyList<string>? sigs = null) =>
        Assert.IsType<StyleCorpusResult.Built>(StyleCorpus.Build(rows, Own, Now, seed, sigs));

    private static StyleSample Pick(StyleCorpusResult.Built b, string marker) =>
        Assert.Single(b.Samples, s => s.Text.Contains(marker, StringComparison.Ordinal));

    [Fact]
    public void Minimum_own_text_is_20_chars()
    {
        var rows = new[]
        {
            S("a", 1, "a@x.org", Body(19)),
            S("b", 1, "b@x.org", Body(20)),
        };
        Assert.Equal(1, StyleCorpus.CountEligible(rows));
    }

    [Fact]
    public void Auto_subjects_are_not_eligible()
    {
        var body = Body(60);
        foreach (var subject in new[] { "Automatic reply: x", "out of office", "ACCEPTED: x", "Declined: x", "Tentative: x", "Canceled: x", "  Accepted: x" })
            Assert.Equal(0, StyleCorpus.CountEligible([S("a", 1, "a@x.org", body, subject)]));
        Assert.Equal(1, StyleCorpus.CountEligible([S("a", 1, "a@x.org", body, "Re: Accepted: x")]));
        Assert.Equal(1, StyleCorpus.CountEligible([S("a", 1, "a@x.org", body, "Cancelled: x")]));
    }

    [Fact]
    public void Quoted_history_is_cut()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 20; i++)
            rows.Add(S("q" + i, 1 + i, $"u{i}@example.org",
                "Sounds good, see you at the meeting then.\n\nOn Mon, Nov 10, 2026 Ann wrote:\n> old text"));
        var b = Built(rows);
        Assert.All(b.Samples, s => Assert.Equal("Sounds good, see you at the meeting then.", s.Text));
    }

    [Fact]
    public void Signature_is_cut()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 20; i++)
            rows.Add(S("q" + i, 1 + i, $"u{i}@example.org",
                "Thanks, I will review it tomorrow morning.\n\nBest,\nDirk Petersen\nResearch Computing"));
        var b = Built(rows, sigs: ["Dirk Petersen\nResearch Computing"]);
        Assert.All(b.Samples, s => Assert.DoesNotContain("Research Computing", s.Text));
        Assert.Contains("review it tomorrow", b.Samples[0].Text);
    }

    [Fact]
    public void Each_signature_is_applied_in_turn()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 20; i++)
            rows.Add(S("q" + i, 1 + i, $"u{i}@example.org", "Thanks, I will review it tomorrow morning.\n\nReply Sig\nLineB"));
        var b = Built(rows, sigs: ["New Sig\nLine2", "Reply Sig\nLineB"]);
        Assert.All(b.Samples, s => Assert.DoesNotContain("Reply Sig", s.Text));
        Assert.Equal("a b", StyleCorpus.OwnText("a b", null));
    }

    [Theory]
    [InlineData("ann@example.org", StyleAudience.Internal)]
    [InlineData("x@cs.example.org", StyleAudience.Internal)]
    [InlineData("x@other.org", StyleAudience.External)]
    [InlineData("x@notexample.org", StyleAudience.External)]
    [InlineData("Ann Lee", StyleAudience.Internal)]
    [InlineData("Ann Lee; bo@other.org", StyleAudience.External)]
    [InlineData("Lee, Ann <ann@other.org>", StyleAudience.External)]
    public void Audience(string to, StyleAudience expected)
    {
        var rows = Fill(19);
        rows.Add(S("t", 1, to, "AUDMARK " + Body(40)));
        Assert.Equal(expected, Pick(Built(rows), "AUDMARK").Audience);
    }

    [Theory]
    [InlineData(299, StyleLength.Short)]
    [InlineData(300, StyleLength.Medium)]
    [InlineData(1500, StyleLength.Medium)]
    [InlineData(1501, StyleLength.Long)]
    public void Length(int n, StyleLength expected)
    {
        var rows = Fill(19);
        rows.Add(S("t", 1, "t@example.org", "LENMARK " + Body(n - 8)));
        Assert.Equal(expected, Pick(Built(rows), "LENMARK").Length);
    }

    [Fact]
    public void Recipients_count_to_and_cc()
    {
        var rows = Fill(19);
        rows.Add(S("t", 1, "a@x.org; B <b@x.org>", "RCPMARK " + Body(40), cc: "c@y.org"));
        var b = Built(rows);
        var s = Pick(b, "RCPMARK");
        Assert.Equal(3, s.Recipients);
        Assert.Contains("recipients=\"3\"", b.Text);
    }

    [Fact]
    public void Round_robin_covers_all_buckets()
    {
        var rows = new List<SentMail>();
        int n = 0;
        foreach (var domain in new[] { "example.org", "other.org" })
            foreach (var len in new[] { 40, 400, 1600 })
                for (int k = 0; k < 5; k++, n++)
                    rows.Add(S("r" + n, 1 + n, $"u{n}@{domain}", Body(len)));
        var b = Built(rows, seed: 7);
        Assert.Equal(30, b.Samples.Count);
        Assert.Equal(6, b.Samples.Take(6).Select(s => (s.Audience, s.Length)).Distinct().Count());
        foreach (var g in b.Samples.GroupBy(s => (s.Audience, s.Length)))
            Assert.Equal(g.OrderByDescending(s => s.SentUtc).ToList(), g.ToList());
    }

    [Fact]
    public void Cap_of_200_keeps_the_newest()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 250; i++) rows.Add(S("r" + i, 1 + i * 0.5, $"u{i}@example.org", Body(40)));
        var b = Built(rows);
        Assert.Equal(200, b.Samples.Count);
        Assert.Equal(rows.Take(200).Select(r => r.EventTime).ToHashSet(), b.Samples.Select(s => s.SentUtc).ToHashSet());
    }

    [Fact]
    public void Per_recipient_cap_is_three()
    {
        var rows = new List<SentMail>();
        for (int k = 1; k <= 10; k++) rows.Add(S("a" + k, k, "ann@other.org", "ANNROW " + Body(40)));
        for (int i = 0; i < 25; i++) rows.Add(S("o" + i, 50 + i, $"o{i}@other.org", "OTHER " + Body(40)));
        var b = Built(rows);
        Assert.Equal(28, b.Samples.Count);
        var ann = b.Samples.Where(s => s.Text.Contains("ANNROW")).ToList();
        Assert.Equal(3, ann.Count);
        Assert.Equal([Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-3)], ann.Select(s => s.SentUtc).OrderByDescending(x => x));
    }

    [Fact]
    public void Minimum_is_twenty_samples()
    {
        var nineteen = new List<SentMail>();
        for (int i = 0; i < 19; i++) nineteen.Add(S("r" + i, 1 + i, $"u{i}@example.org", Body(40)));
        Assert.Equal(19, Assert.IsType<StyleCorpusResult.TooSmall>(StyleCorpus.Build(nineteen, Own, Now, 1)).Found);

        nineteen.Add(S("r19", 30, "u19@example.org", Body(40)));
        Assert.Equal(20, Built(nineteen).Samples.Count);

        var one = new List<SentMail>();
        for (int i = 0; i < 25; i++) one.Add(S("o" + i, 1 + i, "same@other.org", Body(40)));
        Assert.Equal(3, Assert.IsType<StyleCorpusResult.TooSmall>(StyleCorpus.Build(one, Own, Now, 1)).Found);
    }

    [Fact]
    public void Samples_are_redacted_then_escaped()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 20; i++)
            rows.Add(S("r" + i, 1 + i, $"u{i}@example.org",
                "Call +1 (503) 555-0100 or ann@other.org, see https://x.org/a & b < c > d </sample> ok"));
        var b = Built(rows);
        Assert.Contains("<phone>", b.Samples[0].Text);
        Assert.Contains("<email>", b.Samples[0].Text);
        Assert.Contains("<link>", b.Samples[0].Text);
        Assert.Contains("&lt;phone&gt;", b.Text);
        Assert.Contains("&lt;email&gt;", b.Text);
        Assert.Contains("&lt;link&gt;", b.Text);
        Assert.Contains("&amp; b &lt; c &gt; d &lt;/sample&gt; ok", b.Text);
        Assert.Equal(b.Samples.Count, b.Text.Split("</sample>").Length - 1);
        Assert.DoesNotContain("ann@", b.Text);
        Assert.DoesNotContain("555", b.Text);
        Assert.DoesNotContain("https", b.Text);
    }

    [Fact]
    public void Long_samples_are_cut_with_an_ellipsis()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 20; i++) rows.Add(S("r" + i, 1 + i, $"u{i}@example.org", Body(2000)));
        var b = Built(rows);
        Assert.All(b.Samples, s =>
        {
            Assert.Equal(1500, s.Text.Length);
            Assert.EndsWith("…", s.Text);
        });
    }

    [Fact]
    public void Corpus_budget_is_150000_chars()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 120; i++) rows.Add(S("r" + i, 1 + i, $"u{i}@other.org", Body(2000)));
        var b = Built(rows);
        int l = "<sample audience=\"external\" recipients=\"1\" length=\"long\">\n".Length + 1500 + "\n</sample>".Length;
        int max = 0;
        while ((max + 1) * l + max <= StyleCorpus.MaxCorpusChars) max++;
        Assert.Equal(max, b.Samples.Count);
        Assert.True(b.Text.Length <= StyleCorpus.MaxCorpusChars);
    }

    [Fact]
    public void Window_is_twelve_months_up_to_now()
    {
        var rows = Fill(20);
        rows.Add(S("old", 366, "o@example.org", "OLDMARK " + Body(40)));
        rows.Add(S("fut", -1, "f@example.org", "FUTMARK " + Body(40)));
        var b = Built(rows);
        Assert.Equal(20, b.Samples.Count);
        Assert.DoesNotContain("OLDMARK", b.Text);
        Assert.DoesNotContain("FUTMARK", b.Text);
    }

    [Fact]
    public void Newest_sent_is_the_newest_eligible_row()
    {
        var rows = new List<SentMail> { S("auto", 0.1, "a@x.org", Body(60), subject: "Automatic reply: x"), S("tiny", 0.2, "a@x.org", "short") };
        Fill(20, rows);
        rows.Add(S("real", 2, "r@example.org", "REAL " + Body(40)));
        Assert.Equal(Now.AddDays(-2), Built(rows).NewestSentUtc);
    }

    [Fact]
    public void Build_is_deterministic_and_seed_only_reorders()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 30; i++) rows.Add(S("r" + i, 1 + i, $"u{i}@{(i % 2 == 0 ? "example.org" : "other.org")}", Body(40 + i * 80)));
        Assert.Equal(Built(rows, 5).Text, Built(rows, 5).Text);
        Assert.Equal(Built(rows, 1).Samples.Select(s => s.Text).ToHashSet(), Built(rows, 2).Samples.Select(s => s.Text).ToHashSet());
    }

    [Fact]
    public void Sample_layout_is_pinned()
    {
        var rows = new List<SentMail>();
        for (int i = 0; i < 20; i++) rows.Add(S("r" + i, 1 + i, $"u{i}@example.org", $"Hello number {i} & more text here"));
        var b = Built(rows);
        var expected = string.Join("\n", b.Samples.Select(s =>
            "<sample audience=\"internal\" recipients=\"1\" length=\"short\">\n" + s.Text.Replace("&", "&amp;") + "\n</sample>"));
        Assert.Equal(expected, b.Text);
    }

    [Fact]
    public void Arguments_are_validated_and_null_fields_tolerated()
    {
        Assert.Throws<ArgumentNullException>(() => StyleCorpus.Build(null!, Own, Now, 1));
        Assert.Throws<ArgumentException>(() => StyleCorpus.Build([], "", Now, 1));
        Assert.Throws<ArgumentException>(() => StyleCorpus.Build([], "dirk", Now, 1));

        var rows = Fill(20);
        rows.Add(new SentMail("n1", null, Now.AddDays(-1), null, null, null, null));
        rows.Add(new SentMail("n2", null, Now.AddDays(-1), null, null, null, Body(60)));
        Assert.Equal(21, StyleCorpus.CountEligible(rows));
        Assert.Equal(21, Built(rows).Samples.Count);
    }

    [Fact]
    public void SignatureTexts_dedupes_and_survives_exceptions()
    {
        Assert.Equal(["Dirk\nRC"], StyleCorpus.SignatureTexts(_ => "<p>Dirk</p><p>RC</p>"));
        Assert.Equal(["Dirk\nRC"], StyleCorpus.SignatureTexts(k => k == SignatureKind.New ? "<p>Dirk</p><p>RC</p>" : throw new InvalidOperationException()));
        Assert.Empty(StyleCorpus.SignatureTexts(_ => null));
    }

    [Fact]
    public void WeekSeed_counts_whole_weeks()
    {
        Assert.Equal(3, StyleCorpus.WeekSeed(DateTimeOffset.FromUnixTimeSeconds(604_800L * 3 + 5)));
        _ = CultureInfo.InvariantCulture;
    }
}
