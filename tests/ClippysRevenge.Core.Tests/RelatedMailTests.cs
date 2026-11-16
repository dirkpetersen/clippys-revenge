namespace ClippysRevenge.Core.Tests;

public class RelatedMailTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z");

    private static RelatedRow Row(string id, DateTimeOffset? at = null, string folder = "Inbox", string? topic = "auto",
        string? imid = "auto", string? subject = "auto", string sender = "ann@contoso.example") =>
        new(id, imid == "auto" ? "<" + id + "@x>" : imid, topic == "auto" ? "topic-" + id : topic, folder, at ?? Now.AddDays(-5),
            "Ann", sender, null, null, subject == "auto" ? "subject " + id : subject, "body");

    private static RelatedRow Days(string id, double days, string folder = "Inbox") => Row(id, Now.AddDays(-days), folder);

    private static string[] Ids(IReadOnlyList<RelatedRow> rows) => rows.Select(r => r.EntryId).ToArray();

    // 1-12
    [Fact]
    public void Terms_strips_prefixes_stop_words_short_words_and_years()
    {
        Assert.Equal(["budget", "planning"], RelatedMail.Terms("Re: AW: Budget planning for Q3 2026", ""));
    }

    [Fact]
    public void Terms_strips_repeated_prefixes_with_any_colons()
    {
        Assert.Equal(["server", "outage"], RelatedMail.Terms("RE:: Fwd: WG: Server outage", null));
    }

    [Fact]
    public void Terms_all_stop_words_gives_nothing()
    {
        Assert.Empty(RelatedMail.Terms("Thanks for the update", ""));
    }

    [Fact]
    public void Terms_fill_up_from_the_body_by_count_then_first_occurrence()
    {
        Assert.Equal(["cluster", "storage", "quota", "backup"],
            RelatedMail.Terms("Hello", "Cluster storage quota. Cluster storage. Cluster backup."));
    }

    [Fact]
    public void Terms_fill_up_keeps_the_subject_term_and_drops_short_and_numeric_body_words()
    {
        Assert.Equal(["invoice", "payment", "reminder", "overdue"],
            RelatedMail.Terms("Invoice", "Payment reminder for invoice 4711. Payment overdue."));
    }

    [Fact]
    public void Terms_subject_is_capped_at_eight_in_order()
    {
        var subject = "alpha bravo charlie delta echo foxtrot golf hotel india juliet";
        Assert.Equal(["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"], RelatedMail.Terms(subject, ""));
    }

    [Fact]
    public void Terms_are_distinct_and_case_folded()
    {
        Assert.Equal(["budget", "review"], RelatedMail.Terms("Budget budget BUDGET review", ""));
    }

    [Fact]
    public void Terms_keep_letters_with_diacritics_and_drop_german_stop_words()
    {
        Assert.Equal(["café", "öffnungszeiten"], RelatedMail.Terms("Café-Öffnungszeiten über", ""));
    }

    [Fact]
    public void Terms_hostile_input_never_yields_fts_syntax()
    {
        var terms = RelatedMail.Terms("\"x\" OR y* NEAR(z) {subject}: -drop; DELETE", "");
        Assert.Equal(["near", "subject", "drop", "delete"], terms);
        Assert.All(terms, t => Assert.All(t, c => Assert.True(char.IsLetterOrDigit(c))));
    }

    [Fact]
    public void Terms_long_digit_runs_are_kept()
    {
        Assert.Equal(["ticket", "123456"], RelatedMail.Terms("Ticket 123456 update", ""));
    }

    [Fact]
    public void Terms_body_is_cut_at_2000_characters()
    {
        Assert.Empty(RelatedMail.Terms("Hi", new string('.', 2_000) + " kubernetes kubernetes"));
        Assert.Equal(["kubernetes"], RelatedMail.Terms("Hi", new string('.', 1_900) + " kubernetes kubernetes"));
    }

    [Fact]
    public void Terms_null_and_stop_word_list_shape()
    {
        Assert.Empty(RelatedMail.Terms(null, null));
        Assert.All(RelatedMail.StopWords, w =>
        {
            Assert.Equal(w.ToLowerInvariant(), w);
            Assert.True(w.Length >= 3);
        });
        Assert.Contains("the", RelatedMail.StopWords);
        Assert.Contains("und", RelatedMail.StopWords);
        Assert.Contains("vous", RelatedMail.StopWords);
    }

    // 13
    [Fact]
    public void Score_formula()
    {
        Assert.Equal(2.5, RelatedMail.Score(0, false, Now, Now), 9);
        Assert.Equal(1.5, RelatedMail.Score(null, true, Now.AddDays(-365), Now), 9);
        Assert.Equal(3.02, RelatedMail.Score(49, true, Now, Now), 9);
        Assert.Equal(0, RelatedMail.Score(null, false, Now.AddDays(-730), Now), 9);
        Assert.Equal(0.5, RelatedMail.Score(null, false, Now.AddDays(3), Now), 9);
    }

    // 14
    [Fact]
    public void Select_merges_a_row_found_by_both_queries_once_and_adds_the_scores()
    {
        var x = Days("X", 20);
        var others = Enumerable.Range(0, 5).Select(i => Days("T" + i, 20)).ToList();
        // X is the 6th term hit; the people hit lifts it above the other five.
        var termHits = others.Append(x).ToList();
        var r = RelatedMail.Select(new RelatedHits(termHits, [x]), Now);
        Assert.Equal(5, r.Count);
        Assert.Single(r, row => row.EntryId == "X");
        Assert.DoesNotContain(r, row => row.EntryId == "T4");
    }

    // 15
    [Fact]
    public void Select_dedupes_by_internet_message_id_keeping_the_higher_score()
    {
        var a = Row("A", imid: "<a@x>");
        var b = Row("B", folder: "Archive", imid: "<a@x>");
        var r = RelatedMail.Select(new RelatedHits([a, b], []), Now);
        Assert.Equal(["A"], Ids(r));
    }

    // 16
    [Fact]
    public void Select_dedupes_by_topic_ignoring_case_and_spaces()
    {
        var a = Row("A", topic: "Budget");
        var b = Row("B", topic: " budget ");
        Assert.Single(RelatedMail.Select(new RelatedHits([a, b], []), Now));
    }

    // 17
    [Fact]
    public void Select_dedupes_by_subject_sender_and_minute()
    {
        var t = DateTimeOffset.Parse("2026-11-01T12:00:05Z");
        var a = Row("A", t, topic: "t1", imid: null, subject: "Re: Lunch");
        var b = Row("B", t.AddSeconds(45), topic: "t2", imid: null, subject: "lunch");
        Assert.Equal(["A"], Ids(RelatedMail.Select(new RelatedHits([a, b], []), Now)));

        var c = Row("C", t.AddSeconds(65), topic: "t3", imid: null, subject: "lunch");
        Assert.Equal(2, RelatedMail.Select(new RelatedHits([a, c], []), Now).Count);
    }

    // 18
    [Fact]
    public void Select_keeps_the_five_best_oldest_first()
    {
        var rows = Enumerable.Range(1, 7).Select(d => Days("R" + d, d)).ToList();
        var r = RelatedMail.Select(new RelatedHits([], rows), Now);
        Assert.Equal(["R5", "R4", "R3", "R2", "R1"], Ids(r));
    }

    [Fact]
    public void Select_ties_go_to_the_newer_row()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Days("O" + i, 400 + i)).ToList();   // all age part 0, all people hits
        var r = RelatedMail.Select(new RelatedHits([], rows), Now);
        Assert.Equal(5, r.Count);
        Assert.DoesNotContain(r, row => row.EntryId == "O5");
    }

    // 19
    [Fact]
    public void Select_reserves_a_slot_for_a_people_hit_in_sent_items()
    {
        var term = Enumerable.Range(0, 6).Select(i => Days("T" + i, i + 1)).ToList();
        var sent = Days("S", 300, @"Sent Items\Projects");
        var r = RelatedMail.Select(new RelatedHits(term, [sent]), Now);
        Assert.Equal(["S", "T3", "T2", "T1", "T0"], Ids(r));
    }

    // 20
    [Fact]
    public void Select_gives_no_slot_to_a_sent_row_that_is_only_a_term_hit()
    {
        var term = Enumerable.Range(0, 6).Select(i => Days("T" + i, i + 1)).ToList();
        term.Add(Days("S", 300, "Sent Items"));
        var r = RelatedMail.Select(new RelatedHits(term, []), Now);
        Assert.DoesNotContain(r, row => row.EntryId == "S");
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void Select_leaves_the_result_alone_when_a_sent_row_is_already_in()
    {
        var term = Enumerable.Range(0, 6).Select(i => Days("T" + i, i + 1)).ToList();
        var s1 = Days("S1", 2, "Sent Items");
        var s2 = Days("S2", 300, "Sent Items");
        var r = RelatedMail.Select(new RelatedHits(term, [s1, s2]), Now);
        Assert.Contains(r, row => row.EntryId == "S1");
        Assert.DoesNotContain(r, row => row.EntryId == "S2");
        Assert.Equal(5, r.Count);
    }

    [Fact]
    public void Select_appends_the_sent_slot_when_fewer_than_five_are_kept()
    {
        var term = new List<RelatedRow> { Days("T0", 1), Days("T1", 2) };
        var sent = Days("S", 300, "Sent Items");
        Assert.Equal(["S", "T1", "T0"], Ids(RelatedMail.Select(new RelatedHits(term, [sent]), Now)));
    }

    // 21
    [Fact]
    public void Select_small_and_empty_inputs()
    {
        var a = Days("A", 3);
        var b = Days("B", 2);
        Assert.Equal(["A", "B"], Ids(RelatedMail.Select(new RelatedHits([b, a], []), Now)));
        Assert.Empty(RelatedMail.Select(RelatedHits.None, Now));
        Assert.Throws<ArgumentNullException>(() => RelatedMail.Select(null!, Now));
        Assert.Throws<ArgumentNullException>(() => RelatedMail.Select(new RelatedHits(null!, []), Now));
        Assert.Throws<ArgumentNullException>(() => RelatedMail.Select(new RelatedHits([], null!), Now));
    }

    // 22
    [Theory]
    [InlineData(0, 800)]
    [InlineData(1, 800)]
    [InlineData(5, 800)]
    [InlineData(6, 666)]
    [InlineData(25, 200)]
    [InlineData(100, 200)]
    public void PerMessageChars(int count, int expected) => Assert.Equal(expected, RelatedMail.PerMessageChars(count));

    [Fact]
    public void RelatedRow_ToString_shows_no_mail_data()
    {
        Assert.Equal("RelatedRow", Row("A", subject: "SECRET").ToString());
    }
}
