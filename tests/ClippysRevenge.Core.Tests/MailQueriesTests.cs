using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class MailQueriesTests : IDisposable
{
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("Test-8", TimeSpan.FromHours(-8), "Test-8", "Test-8");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-mq-" + Guid.NewGuid().ToString("N"));
    private readonly MailboxIndexes _mi;

    public MailQueriesTests()
    {
        _mi = new MailboxIndexes(_dir, ["s@x.edu"]);
        _mi.For("").UpsertBatch([
            new IndexedEmail { EntryId = "e1", Folder = "Inbox", Subject = "Budget 2026", SenderName = "Ann",
                SenderEmail = "ann@x.edu", To = "me@x.edu", EventTime = new DateTimeOffset(2026, 3, 1, 20, 0, 0, TimeSpan.Zero),
                Body = "the quarterly budget is attached",
                Attachments = [new AttachmentInfo(0, "b.pdf", 10, false), new AttachmentInfo(1, "logo.png", 5, true)] },
            new IndexedEmail { EntryId = "e2", Folder = "Inbox", Subject = "Lunch", EventTime = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), Body = "noon?" }]);
        _mi.For("s@x.edu").UpsertBatch([
            new IndexedEmail { EntryId = "s1", Folder = "Inbox", Subject = "Shared", EventTime = new DateTimeOffset(2026, 3, 3, 0, 0, 0, TimeSpan.Zero), Body = "x" }]);
    }

    public void Dispose()
    {
        _mi.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private IReadOnlyList<SearchResultDto> S(string? query = null, string? dateTo = null, string? mailbox = null, int limit = 50, int offset = 0) =>
        MailQueries.Search(_mi, Tz, query, null, null, null, null, dateTo, null, null, null, null, null, mailbox, limit, offset);

    [Fact]
    public void Search_MapsFieldsAndLocalDate()
    {
        var r = Assert.Single(S("budget"));
        Assert.Equal("e1", r.Id);
        Assert.Equal("", r.Mailbox);
        Assert.Equal("2026-03-01T12:00:00-08:00", r.Date);
        Assert.Equal("Ann", r.From);
        Assert.True(r.HasAttachments);
        Assert.Equal(2, r.Attachments.Count);
        Assert.Contains("**", r.Snippet);
    }

    [Fact]
    public void Search_NoQuery_SnippetIsBodyStart() =>
        Assert.Equal("noon?", S().First(x => x.Id == "e2").Snippet);

    [Fact]
    public void Search_All_TagsMailboxes()
    {
        var r = S(mailbox: "all");
        Assert.Equal(["s@x.edu", "", ""], r.Select(x => x.Mailbox));
    }

    [Fact]
    public void Search_LimitClampedAndOffset()
    {
        Assert.Single(S(limit: 0));
        Assert.Single(S(limit: 1, offset: -5));
        Assert.Equal("e1", Assert.Single(S(limit: 5, offset: 1)).Id);
    }

    [Fact]
    public void Search_BadDate_IsMcpException()
    {
        var ex = Assert.Throws<McpException>(() => S(dateTo: "9999-12-31"));
        Assert.Contains("date_to", ex.Message);
        Assert.Throws<McpException>(() => S(dateTo: "yesterday"));
    }

    [Fact]
    public void Search_UnknownMailbox_Throws() =>
        Assert.Throws<McpException>(() => S(mailbox: "nobody@y.org"));

    [Fact]
    public void GetIndexed_FindsInSharedAndMissesUnknown()
    {
        var m = MailQueries.GetIndexed(_mi, Tz, "s1");
        Assert.NotNull(m);
        Assert.Equal("s@x.edu", m.Mailbox);
        Assert.Equal("index", m.Source);
        Assert.Null(MailQueries.GetIndexed(_mi, Tz, "zzz"));
        Assert.Null(MailQueries.GetIndexed(_mi, Tz, ""));
    }

    [Fact]
    public void GetIndexed_FullBodyAndAttachments()
    {
        var m = MailQueries.GetIndexed(_mi, Tz, "e1")!;
        Assert.Equal("the quarterly budget is attached", m.Body);
        Assert.Equal(2, m.Attachments.Count);
        Assert.True(m.Attachments[1].Inline);
    }

    [Fact]
    public void FromLive_MapsAndSetsSource()
    {
        var e = new IndexedEmail { EntryId = "L", Folder = "Inbox", EventTime = DateTimeOffset.UnixEpoch,
            Attachments = [new AttachmentInfo(0, "i.png", 1, true)] };
        var m = MailQueries.FromLive(new LiveEmail("", e), Tz);
        Assert.Equal("outlook", m.Source);
        Assert.False(m.HasAttachments);
        Assert.Equal("", m.Subject);
    }

    [Fact]
    public void Status_AllAndOne()
    {
        using var hub = new ActivityHub();
        var all = MailQueries.Status(_mi, hub, Tz, true, null);
        Assert.Equal(["", "s@x.edu"], all.Mailboxes.Select(x => x.Mailbox));
        Assert.Equal(2, all.Mailboxes[0].TotalEmails);
        Assert.True(all.OutlookRunning);
        Assert.Equal("Idle", all.State);
        var one = MailQueries.Status(_mi, hub, Tz, null, " S@X.edu ");
        Assert.Equal(1, Assert.Single(one.Mailboxes).TotalEmails);
        Assert.Null(one.OutlookRunning);
        Assert.Throws<McpException>(() => MailQueries.Status(_mi, hub, Tz, null, "no@y.org"));
    }
}

public class SearchDatesRangeTests
{
    [Theory]
    [InlineData("9999-12-31")]
    [InlineData("9999-12-31T23:59:59-14:00")]
    public void ParseTo_OutOfRange_IsFormatException(string text)
    {
        var ex = Record.Exception(() => SearchDates.ParseTo(text, TimeZoneInfo.Utc));
        Assert.IsType<FormatException>(ex);
        Assert.StartsWith("date_to \"" + text + "\" is not a valid date.", ex.Message);
    }
}
