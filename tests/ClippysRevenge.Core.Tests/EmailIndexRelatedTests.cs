using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexRelatedTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z");
    private static readonly DateTimeOffset AnchorTime = Now.AddHours(-1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-rel-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;

    public EmailIndexRelatedTests() => _ix = new EmailIndex(Path.Combine(_dir, "index.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IndexedEmail Mail(string id, DateTimeOffset? at = null, string folder = "Inbox", string subject = "unrelated",
        string body = "nothing", string? topic = null, string? imid = null, string? irt = null, string sender = "zed@other.example",
        string senderName = "Zed", string? to = null, string? cc = null, IReadOnlyList<AttachmentInfo>? atts = null) => new()
    {
        EntryId = id,
        Folder = folder,
        Subject = subject,
        Body = body,
        ConversationTopic = topic ?? "topic-" + id,
        InternetMessageId = imid ?? "<" + id + "@x>",
        InReplyTo = irt,
        SenderEmail = sender,
        SenderName = senderName,
        To = to,
        Cc = cc,
        EventTime = at ?? Now.AddDays(-10),
        Attachments = atts ?? [],
    };

    private void Seed(params IndexedEmail[] rows) => _ix.UpsertBatch(rows);

    private void SeedAnchor() =>
        Seed(Mail("A", AnchorTime, subject: "Budget planning", topic: "Budget", imid: "<a@x>", sender: "ann@contoso.example"));

    private RelatedHits Query(IReadOnlyList<string>? terms = null, string? sender = null, DateTimeOffset? since = null,
        DateTimeOffset? until = null) =>
        _ix.RelatedCandidates(new RelatedQuery("A", "<a@x>", "Budget", sender, terms ?? [], since ?? Now.AddMonths(-12),
            until ?? AnchorTime));

    private static string[] Ids(IReadOnlyList<RelatedRow> rows) => rows.Select(r => r.EntryId).ToArray();

    // 23
    [Fact]
    public void Window_excludes_older_than_twelve_months_and_newer_than_the_anchor()
    {
        SeedAnchor();
        Seed(Mail("OLD", Now.AddMonths(-13), subject: "budget"),
            Mail("OK", Now.AddMonths(-11), subject: "budget"),
            Mail("NEW", AnchorTime.AddMinutes(5), subject: "budget"),
            Mail("SAME", AnchorTime, subject: "budget"));
        var hits = Query(["budget"]);
        Assert.Equal(["OK", "SAME"], Ids(hits.TermHits).Order().ToArray());
    }

    // 24
    [Fact]
    public void Exclusions_apply_to_both_queries_and_controls_are_returned()
    {
        SeedAnchor();
        var s = "ann@contoso.example";
        Seed(Mail("COPY", subject: "budget", imid: "<a@x>", folder: "Archive", sender: s),
            Mail("TOPIC", subject: "budget", topic: "BUDGET", sender: s),
            Mail("IRT", subject: "budget", irt: "<a@x>", sender: s),
            Mail("DR", subject: "budget", folder: "Drafts", sender: s),
            Mail("DR2", subject: "budget", folder: @"Drafts\Old", sender: s),
            Mail("DEL", subject: "budget", folder: "Deleted Items", sender: s),
            Mail("DEL2", subject: "budget", folder: @"Deleted Items\X", sender: s));
        var hits = Query(["budget"], s);
        Assert.Empty(hits.TermHits);
        Assert.Empty(hits.PeopleHits);

        Seed(Mail("C1", subject: "budget", sender: s),
            Mail("C2", subject: "budget", folder: "Archive", sender: s),
            Mail("C3", subject: "budget", folder: "Drafts2", sender: s),
            Mail("C4", subject: "budget", folder: "My Drafts", sender: s));
        hits = Query(["budget"], s);
        Assert.Equal(["C1", "C2", "C3", "C4"], Ids(hits.TermHits).Order().ToArray());
        Assert.Equal(["C1", "C2", "C3", "C4"], Ids(hits.PeopleHits).Order().ToArray());
    }

    // 25
    [Fact]
    public void Terms_match_subject_and_body_only()
    {
        SeedAnchor();
        Seed(Mail("S", subject: "budget"),
            Mail("B", body: "the budget is tight"),
            Mail("N", subject: "x", senderName: "Budget Person", atts: [new AttachmentInfo(1, "budget.pdf", 10, false)]));
        Assert.Equal(["B", "S"], Ids(Query(["budget"]).TermHits).Order().ToArray());
    }

    [Fact]
    public void A_row_with_both_terms_ranks_before_a_row_with_one()
    {
        SeedAnchor();
        Seed(Mail("ONE", subject: "budget", at: Now.AddDays(-1)), Mail("TWO", subject: "budget planning", at: Now.AddDays(-20)));
        Assert.Equal(["TWO", "ONE"], Ids(Query(["budget", "planning"]).TermHits));
    }

    [Fact]
    public void Terms_query_is_limited_to_fifty_rows()
    {
        SeedAnchor();
        Seed(Enumerable.Range(0, 60).Select(i => Mail("M" + i, subject: "budget", at: Now.AddDays(-2).AddMinutes(-i))).ToArray());
        Assert.Equal(50, Query(["budget"]).TermHits.Count);
    }

    // 26
    [Fact]
    public void People_query_finds_sender_and_sent_to_or_cc()
    {
        SeedAnchor();
        Seed(Mail("FROM", sender: "ANN@contoso.example", at: Now.AddDays(-5)),
            Mail("SENT", folder: "Sent Items", to: "Ann <ann@contoso.example>", at: Now.AddDays(-4)),
            Mail("SUB", folder: @"Sent Items\Sub", to: "x@y.example", cc: "Boss <boss@y.example>; ann@contoso.example", at: Now.AddDays(-3)),
            Mail("JANN", folder: "Sent Items", to: "jann@contoso.example", at: Now.AddDays(-2)),
            Mail("INBOXTO", to: "ann@contoso.example", at: Now.AddDays(-1)));
        var hits = Query(sender: "ann@contoso.example");
        Assert.Equal(["SUB", "SENT", "FROM"], Ids(hits.PeopleHits));
        Assert.Empty(hits.TermHits);
    }

    [Fact]
    public void People_query_is_limited_to_fifty_rows()
    {
        SeedAnchor();
        Seed(Enumerable.Range(0, 60).Select(i => Mail("M" + i, sender: "ann@contoso.example", at: Now.AddDays(-2).AddMinutes(-i))).ToArray());
        Assert.True(Query(sender: "ann@contoso.example").PeopleHits.Count <= 50);
    }

    // 27
    [Fact]
    public void Missing_sender_or_terms_skip_their_query()
    {
        SeedAnchor();
        Seed(Mail("M", subject: "budget", sender: "ann@contoso.example"));
        Assert.Empty(Query(["budget"], null).PeopleHits);
        Assert.Empty(Query(["budget"], "  ").PeopleHits);
        Assert.Empty(Query([], "ann@contoso.example").TermHits);
        var none = Query([], null);
        Assert.Empty(none.TermHits);
        Assert.Empty(none.PeopleHits);
    }

    // 28
    [Fact]
    public void Hostile_terms_are_dropped_without_an_exception()
    {
        SeedAnchor();
        Seed(Mail("M", subject: "budget"));
        var hostile = RelatedMail.Terms("\"x\" OR y* NEAR(z) {subject}: -drop; DELETE", "");
        _ = Query(hostile);
        Assert.Empty(Query(["a\"b", "x*", "near(1", "OR", ""]).TermHits);
        Assert.Equal(["M"], Ids(Query(["a\"b", "budget"]).TermHits));
    }

    // 29
    [Fact]
    public void Null_query_throws_and_bodies_are_cut_to_8000()
    {
        Assert.Throws<ArgumentNullException>(() => _ix.RelatedCandidates(null!));
        SeedAnchor();
        Seed(Mail("BIG", subject: "budget", body: new string('x', 20_000)));
        var row = Assert.Single(Query(["budget"]).TermHits);
        Assert.Equal(8_000, row.Body!.Length);
    }

    [Fact]
    public void Rows_carry_their_columns()
    {
        SeedAnchor();
        Seed(Mail("M", at: Now.AddDays(-3), subject: "budget", to: "Me <me@example.org>", cc: "c@x.org", sender: "bob@x.org", senderName: "Bob"));
        var r = Assert.Single(Query(["budget"]).TermHits);
        Assert.Equal(("M", "<M@x>", "topic-M", "Inbox", Now.AddDays(-3), "Bob", "bob@x.org", "Me <me@example.org>", "c@x.org", "budget"),
            (r.EntryId, r.InternetMessageId, r.ConversationTopic, r.Folder, r.EventTime, r.SenderName, r.SenderEmail, r.To, r.Cc, r.Subject));
    }
}
