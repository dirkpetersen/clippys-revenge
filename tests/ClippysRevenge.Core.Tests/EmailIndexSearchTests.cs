using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-sx-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;

    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int n) => Base.AddHours(n);

    public EmailIndexSearchTests()
    {
        _ix = new EmailIndex(Path.Combine(_dir, "index.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IndexedEmail Build(string id, DateTimeOffset t, string folder = "Inbox", string subject = "",
        string senderName = "", string senderEmail = "", string to = "", string cc = "", string? body = "",
        bool unread = false, params (string name, bool inline)[] atts) => new()
    {
        EntryId = id,
        Folder = folder,
        Subject = subject,
        SenderName = senderName,
        SenderEmail = senderEmail,
        To = to,
        Cc = cc,
        Body = body,
        Unread = unread,
        EventTime = t,
        Attachments = atts.Select((a, i) => new AttachmentInfo(i + 1, a.name, 100, a.inline)).ToList(),
    };

    private void Mail(string id, DateTimeOffset t, string folder = "Inbox", string subject = "",
        string senderName = "", string senderEmail = "", string to = "", string cc = "", string? body = "",
        bool unread = false, params (string name, bool inline)[] atts) =>
        _ix.UpsertBatch([Build(id, t, folder, subject, senderName, senderEmail, to, cc, body, unread, atts)]);

    private void SeedA()
    {
        _ix.UpsertBatch([
            Build("a1", T(1), "Inbox", "Budget 2026 review", "Alice Smith", "alice@example.com",
                "Bob Jones <bob@contoso.com>", "", "Please review the budget-2026 sheet", true, ("budget.xlsx", false)),
            Build("a2", T(2), "Sent Items", "Lunch", "Me", "me@contoso.com",
                "Smith, Carol <carol@fabrikam.com>", "Dan <dan@contoso.com>", "Café at noon?", false, ("logo.png", true)),
            Build("a3", T(3), "Inbox\\Projects", "Re:: Project plan", "Bob Jones", "bob@contoso.com",
                "Me <me@contoso.com>", "", "See attached plan", false, ("Plan.PDF", false), ("sig.png", true)),
            Build("a4", T(4), "Inbox", "O'Example's notes", "Eve O'Example", "eve@example.org",
                "Me <me@contoso.com>", "alice@example.com", "100% done_now", true),
        ]);
    }

    private string[] Ids(SearchFilter f) => _ix.Search(f).Select(r => r.Id).ToArray();
    private static string[] A(params string[] s) => s;

    [Fact]
    public void NoFilters_ReturnsAllNewestFirst()
    {
        SeedA();
        Assert.Equal(A("a4", "a3", "a2", "a1"), Ids(new SearchFilter()));
    }

    [Fact]
    public void Query_MatchesBody()
    {
        SeedA();
        Assert.Equal(A("a1"), Ids(new SearchFilter { Query = "sheet" }));
    }

    [Fact]
    public void Query_MatchesAttachmentName()
    {
        SeedA();
        Assert.Equal(A("a1"), Ids(new SearchFilter { Query = "budget" }));
        Assert.Equal(A("a3"), Ids(new SearchFilter { Query = "plan" }));
    }

    [Fact]
    public void Query_PunctuationIsSafe()
    {
        SeedA();
        Assert.Equal(A("a1"), Ids(new SearchFilter { Query = "budget-2026" }));
        Assert.Equal(A("a4"), Ids(new SearchFilter { Query = "O'Example" }));
        Assert.Empty(Ids(new SearchFilter { Query = "C++" }));
    }

    [Fact]
    public void Query_Prefix()
    {
        SeedA();
        Assert.Equal(A("a1"), Ids(new SearchFilter { Query = "budg*" }));
    }

    [Fact]
    public void Query_OnlyJunk_NoMatchClause()
    {
        SeedA();
        var r = _ix.Search(new SearchFilter { Query = "--- ***" });
        Assert.Equal(A("a4", "a3", "a2", "a1"), r.Select(x => x.Id).ToArray());
        Assert.Equal("See attached plan", r[1].Snippet);
    }

    [Fact]
    public void Query_Diacritics()
    {
        SeedA();
        var r = _ix.Search(new SearchFilter { Query = "cafe" });
        Assert.Equal(A("a2"), r.Select(x => x.Id).ToArray());
        Assert.Contains("**Café**", r[0].Snippet);
    }

    [Fact]
    public void Query_SenderEmailPhrase()
    {
        SeedA();
        Assert.Equal(A("a4", "a1"), Ids(new SearchFilter { Query = "alice@example.com" }));
    }

    [Fact]
    public void Sender_SubstringOfNameOrEmail()
    {
        SeedA();
        Assert.Equal(A("a1"), Ids(new SearchFilter { Sender = "smith" }));
        Assert.Equal(A("a3", "a2"), Ids(new SearchFilter { Sender = "CONTOSO" }));
        Assert.Empty(Ids(new SearchFilter { Sender = "nobody" }));
    }

    [Fact]
    public void Recipient_SubstringToOrCc()
    {
        SeedA();
        Assert.Equal(A("a2"), Ids(new SearchFilter { Recipient = "dan@" }));
        Assert.Equal(A("a2"), Ids(new SearchFilter { Recipient = "carol" }));
        Assert.Equal(A("a4", "a3"), Ids(new SearchFilter { Recipient = "me@contoso.com" }));
    }

    [Fact]
    public void Recipient_RawList_MatchesAny()
    {
        SeedA();
        Assert.Equal(A("a2", "a1"),
            Ids(new SearchFilter { Recipient = "Smith, Carol <carol@fabrikam.com>; bob@contoso.com" }));
    }

    [Fact]
    public void Recipient_UnparsableList_FallsBackToSubstring()
    {
        SeedA();
        Assert.Empty(Ids(new SearchFilter { Recipient = "Bob <not-an-email>" }));
    }

    [Fact]
    public void Subject_Substring()
    {
        SeedA();
        Assert.Equal(A("a3"), Ids(new SearchFilter { Subject = "PLAN" }));
        Assert.Equal(A("a3"), Ids(new SearchFilter { Subject = "re::" }));
    }

    [Fact]
    public void Like_WildcardsAreLiteral()
    {
        SeedA();
        Assert.Empty(Ids(new SearchFilter { Subject = "%" }));
        Assert.Empty(Ids(new SearchFilter { Sender = "_" }));
        Mail("a5", T(5), subject: "100%");
        Assert.Equal(A("a5"), Ids(new SearchFilter { Subject = "0%" }));
    }

    [Fact]
    public void DateFrom_Inclusive()
    {
        SeedA();
        Assert.Equal(A("a4", "a3", "a2"), Ids(new SearchFilter { DateFrom = T(2) }));
    }

    [Fact]
    public void DateTo_Inclusive()
    {
        SeedA();
        Assert.Equal(A("a2", "a1"), Ids(new SearchFilter { DateTo = T(2) }));
    }

    [Fact]
    public void DateRange_Inverted_Empty()
    {
        SeedA();
        Assert.Empty(Ids(new SearchFilter { DateFrom = T(3), DateTo = T(1) }));
    }

    [Fact]
    public void Folder_Aliases()
    {
        SeedA();
        Assert.Equal(A("a4", "a1"), Ids(new SearchFilter { Folder = "inbox" }));
        Assert.Equal(A("a2"), Ids(new SearchFilter { Folder = "sent" }));
        Assert.Equal(A("a2"), Ids(new SearchFilter { Folder = "SENT ITEMS" }));
        Assert.Equal(A("a3"), Ids(new SearchFilter { Folder = "inbox/projects" }));
        Assert.Equal(A("a3"), Ids(new SearchFilter { Folder = "\\Inbox\\Projects\\" }));
        Assert.Equal(4, Ids(new SearchFilter { Folder = "" }).Length);
        Assert.Equal(4, Ids(new SearchFilter { Folder = "  " }).Length);
        Assert.Empty(Ids(new SearchFilter { Folder = "Archive" }));
    }

    [Fact]
    public void HasAttachment_IgnoresInline()
    {
        SeedA();
        Assert.Equal(A("a3", "a1"), Ids(new SearchFilter { HasAttachment = true }));
        Assert.Equal(A("a4", "a2"), Ids(new SearchFilter { HasAttachment = false }));
    }

    [Fact]
    public void AttachmentName_AnyIncludingInline()
    {
        SeedA();
        Assert.Equal(A("a3"), Ids(new SearchFilter { AttachmentName = "plan" }));
        Assert.Equal(A("a2"), Ids(new SearchFilter { AttachmentName = "logo" }));
        Assert.Equal(A("a3", "a2"), Ids(new SearchFilter { AttachmentName = "PNG" }));
    }

    [Theory]
    [InlineData("pdf", "a3")]
    [InlineData(".pdf", "a3")]
    [InlineData("PDF", "a3")]
    [InlineData("xlsx", "a1")]
    [InlineData("df", "")]
    public void AttachmentExt_WithOrWithoutDot(string ext, string expected)
    {
        SeedA();
        var exp = expected.Length == 0 ? [] : new[] { expected };
        Assert.Equal(exp, Ids(new SearchFilter { AttachmentExt = ext }));
    }

    [Fact]
    public void AttachmentExt_DotOnly_IsNoFilter()
    {
        SeedA();
        Assert.Equal(4, Ids(new SearchFilter { AttachmentExt = "." }).Length);
    }

    [Fact]
    public void AttachmentExt_ExactLastExtension()
    {
        Mail("b1", T(1), atts: [("report.pdf.zip", false)]);
        Mail("b2", T(2), atts: [("pdf", false)]);
        Assert.Empty(Ids(new SearchFilter { AttachmentExt = "pdf" }));
        Assert.Equal(A("b1"), Ids(new SearchFilter { AttachmentExt = "zip" }));
        Assert.Equal(A("b1"), Ids(new SearchFilter { AttachmentExt = "pdf.zip" }));
    }

    [Fact]
    public void Unread()
    {
        SeedA();
        Assert.Equal(A("a4", "a1"), Ids(new SearchFilter { Unread = true }));
        Assert.Equal(A("a3", "a2"), Ids(new SearchFilter { Unread = false }));
    }

    [Fact]
    public void Combined_AllFiltersAnded()
    {
        SeedA();
        var f = new SearchFilter
        {
            Query = "review", Sender = "alice", Recipient = "bob@", Subject = "budget",
            DateFrom = T(0), DateTo = T(1), Folder = "inbox", HasAttachment = true,
            AttachmentName = "budget", AttachmentExt = "xlsx", Unread = true,
        };
        Assert.Equal(A("a1"), Ids(f));
        Assert.Empty(Ids(f with { Unread = false }));
    }

    [Fact]
    public void Combined_QueryPlusFolder()
    {
        SeedA();
        Assert.Empty(Ids(new SearchFilter { Query = "plan", Folder = "inbox" }));
        Assert.Equal(A("a3"), Ids(new SearchFilter { Query = "plan", Folder = "Inbox\\Projects" }));
    }

    [Fact]
    public void Result_Fields()
    {
        SeedA();
        var r = _ix.Search(new SearchFilter { Subject = "plan" }).Single();
        Assert.Equal("a3", r.Id);
        Assert.Equal("Inbox\\Projects", r.Folder);
        Assert.Equal(T(3), r.Date);
        Assert.Equal(TimeSpan.Zero, r.Date.Offset);
        Assert.Equal("Bob Jones", r.From);
        Assert.Equal("bob@contoso.com", r.SenderEmail);
        Assert.Equal("Me <me@contoso.com>", r.To);
        Assert.Equal("Re:: Project plan", r.Subject);
        Assert.True(r.HasAttachments);
        Assert.Equal(
            [new AttachmentInfo(1, "Plan.PDF", 100, false), new AttachmentInfo(2, "sig.png", 100, true)],
            r.Attachments);
        Assert.Equal("", r.Mailbox);
    }

    [Fact]
    public void Result_NoAttachments_EmptyList()
    {
        SeedA();
        var r = _ix.Search(new SearchFilter { Subject = "notes" }).Single();
        Assert.NotNull(r.Attachments);
        Assert.Empty(r.Attachments);
    }

    [Fact]
    public void Snippet_Fts_HighlightsTerm()
    {
        SeedA();
        var r = _ix.Search(new SearchFilter { Query = "sheet" }).Single();
        Assert.Contains("**sheet**", r.Snippet);
        Assert.DoesNotContain('\n', r.Snippet);
        Assert.DoesNotContain('\r', r.Snippet);
    }

    [Fact]
    public void Snippet_NoQuery_First200CollapsedWhitespace()
    {
        Mail("s1", T(1), body: "Line one\r\n\r\n   Line\ttwo");
        Mail("s2", T(2), body: new string('x', 500));
        Mail("s3", T(3), body: null);
        Mail("s4", T(4), body: "");
        var r = _ix.Search(new SearchFilter()).ToDictionary(x => x.Id);
        Assert.Equal("Line one Line two", r["s1"].Snippet);
        Assert.Equal(200, r["s2"].Snippet.Length);
        Assert.Equal("", r["s3"].Snippet);
        Assert.Equal("", r["s4"].Snippet);
    }

    [Fact]
    public void Snippet_NoSurrogateSplit()
    {
        Mail("e1", T(1), body: new string('x', 199) + "😀" + "tail");
        var s = _ix.Search(new SearchFilter()).Single().Snippet;
        Assert.Equal(199, s.Length);
        Assert.EndsWith("x", s);
    }

    [Fact]
    public void Snippet_FtsMatchOnlyInSubject_NonEmpty()
    {
        SeedA();
        var r = _ix.Search(new SearchFilter { Query = "lunch" }).Single();
        Assert.Equal("a2", r.Id);
        Assert.False(string.IsNullOrWhiteSpace(r.Snippet));
    }

    private void SeedTen()
    {
        for (var i = 0; i < 10; i++) Mail("p" + i, T(i));
    }

    [Fact]
    public void Paging_LimitOffset()
    {
        SeedTen();
        Assert.Equal(A("p9", "p8", "p7"), Ids(new SearchFilter { Limit = 3, Offset = 0 }));
        Assert.Equal(A("p6", "p5", "p4"), Ids(new SearchFilter { Limit = 3, Offset = 3 }));
        Assert.Equal(A("p0"), Ids(new SearchFilter { Limit = 3, Offset = 9 }));
        Assert.Empty(Ids(new SearchFilter { Limit = 3, Offset = 10 }));
    }

    [Fact]
    public void Paging_StableOnEqualTimes()
    {
        for (var i = 0; i < 5; i++) Mail("q" + i, T(1));
        var all = new List<string>();
        foreach (var off in new[] { 0, 2, 4 })
            all.AddRange(Ids(new SearchFilter { Limit = 2, Offset = off }));
        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Distinct().Count());
    }

    private void SeedMany()
    {
        _ix.UpsertBatch(Enumerable.Range(0, 205).Select(i => Build("m" + i, T(i))).ToList());
    }

    [Fact]
    public void Paging_Clamp()
    {
        SeedMany();
        Assert.Equal(200, _ix.Search(new SearchFilter { Limit = 500 }).Count);
        Assert.Single(_ix.Search(new SearchFilter { Limit = 0 }));
        Assert.Single(_ix.Search(new SearchFilter { Limit = -5 }));
        Assert.Equal(Ids(new SearchFilter { Offset = 0 }), Ids(new SearchFilter { Offset = -3 }));
        Assert.Equal(50, _ix.Search(new SearchFilter()).Count);
    }

    [Fact]
    public void SearchPage_Unclamped()
    {
        SeedMany();
        var f = new SearchFilter();
        Assert.Equal(205, _ix.SearchPage(f, 0, 205).Count);
        Assert.Equal(5, _ix.SearchPage(f, 200, 10).Count);
        Assert.Empty(_ix.SearchPage(f, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SearchPage(f, -1, 5));
    }

    [Fact]
    public void Injection_IsInert()
    {
        SeedA();
        Assert.Empty(Ids(new SearchFilter { Subject = "'; DROP TABLE emails; --" }));
        Assert.Empty(Ids(new SearchFilter { Sender = "\" OR 1=1 --" }));
        Assert.Empty(Ids(new SearchFilter { Query = "\") OR (\"x" }));
        Assert.Equal(4, Ids(new SearchFilter()).Length);
    }

    [Fact]
    public void Search_NullFilter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _ix.Search(null!));
    }

    [Fact]
    public void Search_AfterUpdate_SeesNewValues()
    {
        SeedA();
        Mail("a1", T(1), "Inbox", "Changed", "Alice Smith", "alice@example.com",
            "Bob Jones <bob@contoso.com>", "", "Please review the budget-2026 sheet", true, ("budget.xlsx", false));
        Assert.Equal(A("a1"), Ids(new SearchFilter { Subject = "changed" }));
        Assert.Equal(A("a1"), Ids(new SearchFilter { Query = "Budget" }));
        Assert.Equal(A("a1"), Ids(new SearchFilter { Query = "changed" }));
    }

    [Fact]
    public void Search_AfterDelete_Gone()
    {
        SeedA();
        _ix.Delete(["a1"]);
        Assert.Empty(Ids(new SearchFilter { Query = "sheet" }));
        Assert.Empty(Ids(new SearchFilter { AttachmentExt = "xlsx" }));
    }

    [Fact]
    public void Search_AfterSetUnread()
    {
        SeedA();
        _ix.SetUnread([("a1", false)]);
        Assert.Equal(A("a4"), Ids(new SearchFilter { Unread = true }));
    }

    [Fact]
    public void BuildFilter_Empty()
    {
        var f = EmailIndex.BuildFilter(new SearchFilter());
        Assert.Equal("emails e", f.From);
        Assert.Equal("", f.Where);
        Assert.Null(f.FtsMatch);
        Assert.Empty(f.Parameters.ParameterNames);
    }

    [Fact]
    public void BuildFilter_FtsFolderUnread()
    {
        var f = EmailIndex.BuildFilter(new SearchFilter { Query = "budget", Folder = "sent", Unread = true });
        Assert.Contains("emails_fts JOIN emails e", f.From);
        Assert.StartsWith(" WHERE emails_fts MATCH @q", f.Where);
        Assert.Equal(FtsQuery.Build("budget"), f.FtsMatch);
        var names = f.Parameters.ParameterNames.ToArray();
        Assert.Contains("q", names);
        Assert.Contains("folder", names);
        Assert.Contains("u", names);
        Assert.Equal("Sent Items", f.Parameters.Get<string>("folder"));
        Assert.Equal(1, f.Parameters.Get<int>("u"));
    }

    [Fact]
    public void BuildFilter_UnsearchableQuery_NoMatch()
    {
        var f = EmailIndex.BuildFilter(new SearchFilter { Query = "!!!" });
        Assert.Null(f.FtsMatch);
        Assert.Equal("emails e", f.From);
    }

    [Fact]
    public void BuildFilter_NullThrows() =>
        Assert.Throws<ArgumentNullException>(() => EmailIndex.BuildFilter(null!));
}
