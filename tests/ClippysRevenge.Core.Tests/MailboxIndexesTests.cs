using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class MailboxIndexesTests : IDisposable
{
    private const string A = "a@x.edu";
    private const string B = "b@x.edu";
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int n) => Base.AddDays(n);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-mi-" + Guid.NewGuid().ToString("N"));
    private MailboxIndexes? _mi;

    private MailboxIndexes Make(params string[] shared) => _mi = new MailboxIndexes(_dir, shared);

    public void Dispose()
    {
        _mi?.Dispose();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IndexedEmail Email(string id, DateTimeOffset time, string folder = "Inbox", string subject = "s") =>
        new() { EntryId = id, Folder = folder, Subject = subject, EventTime = time, Body = "b" };

    private static void Put(EmailIndex ix, params IndexedEmail[] e) => ix.UpsertBatch(e);

    private static string Db(string dir, string name) => Path.Combine(dir, name);

    private static readonly SearchFilter All50 = new() { Limit = 50 };

    private const string UnknownMsg =
        "Mailbox \"x@y.org\" is not configured or not enabled. Available mailboxes: \"\" (your own mailbox), \"a@x.edu\", \"b@x.edu\". To use another shared mailbox, ask the user to enable it in the Clippy tray menu under Shared mailboxes.";

    // ---- construction and paths

    [Fact]
    public void Ctor_OwnOnly_CreatesIndexDb()
    {
        var mi = Make();
        Assert.True(File.Exists(Db(_dir, "index.db")));
        Assert.Equal([""], mi.Mailboxes);
    }

    [Fact]
    public void Ctor_CreatesMissingDataDir()
    {
        Assert.False(Directory.Exists(_dir));
        Make();
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Ctor_Shared_SeparateFilesPerMailbox()
    {
        var mi = Make("Helpdesk@X.edu", "b@x.edu");
        Assert.True(File.Exists(Db(_dir, "index.db")));
        Assert.True(File.Exists(Db(_dir, "index-helpdesk@x.edu.db")));
        Assert.True(File.Exists(Db(_dir, "index-b@x.edu.db")));
        Assert.Equal(["", "helpdesk@x.edu", "b@x.edu"], mi.Mailboxes);
    }

    [Fact]
    public void Ctor_DuplicateShared_Deduped()
    {
        var mi = Make("a@x.edu", " A@X.EDU ");
        Assert.Equal(["", A], mi.Mailboxes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("all")]
    [InlineData("nope")]
    public void Ctor_InvalidShared_Throws(string bad) =>
        Assert.ThrowsAny<ArgumentException>(() => new MailboxIndexes(_dir, [bad]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Ctor_BlankDataDir_Throws(string? dir) =>
        Assert.ThrowsAny<ArgumentException>(() => new MailboxIndexes(dir!, []));

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData("ALL", "all")]
    [InlineData(" All ", "all")]
    [InlineData(" Help@X.EDU ", "help@x.edu")]
    public void Normalize_Works(string? input, string expected) =>
        Assert.Equal(expected, MailboxIndexes.Normalize(input));

    [Theory]
    [InlineData("", "index.db")]
    [InlineData(null, "index.db")]
    [InlineData("Help@X.EDU", "index-help@x.edu.db")]
    [InlineData("a/b@x.edu", "index-a_b@x.edu.db")]
    public void PathFor_Works(string? mailbox, string file) =>
        Assert.Equal(Path.Combine("d", file), MailboxIndexes.PathFor("d", mailbox));

    [Fact]
    public void PathFor_All_Throws() =>
        Assert.Throws<ArgumentException>(() => MailboxIndexes.PathFor("d", "all"));

    // ---- For

    [Fact]
    public void For_SameInstanceIgnoringCaseAndSpace()
    {
        var mi = Make(A);
        Assert.Same(mi.For("a@x.edu"), mi.For(" A@X.edu "));
        Assert.Same(mi.For(""), mi.For(null));
    }

    [Fact]
    public void For_Unknown_ThrowsMcpExceptionListingMailboxes()
    {
        var mi = Make(A, B);
        var ex = Assert.Throws<McpException>(() => mi.For("x@y.org"));
        Assert.Equal(UnknownMsg, ex.Message);
    }

    [Fact]
    public void For_Unknown_OwnOnly()
    {
        var mi = Make();
        var ex = Assert.Throws<McpException>(() => mi.For("x@y.org"));
        Assert.Contains("Available mailboxes: \"\" (your own mailbox). To use", ex.Message);
    }

    [Fact]
    public void For_All_Throws()
    {
        var mi = Make(A);
        var ex = Assert.Throws<McpException>(() => mi.For("ALL"));
        Assert.Equal("Mailbox \"all\" only works with search tools. Use one mailbox: \"\" (your own mailbox), \"a@x.edu\".", ex.Message);
    }

    [Fact]
    public void For_LongInput_Truncated()
    {
        var mi = Make();
        var input = new string('x', 150) + "@y.org";
        var ex = Assert.Throws<McpException>(() => mi.For(input));
        Assert.Contains(new string('x', 100), ex.Message);
        Assert.DoesNotContain(new string('x', 101), ex.Message);
    }

    // ---- Search

    [Fact]
    public void Search_Isolation()
    {
        var mi = Make(A);
        Put(mi.For(""), Email("own1", T(1)));
        Put(mi.For(A), Email("a1", T(2)));
        var own = Assert.Single(mi.Search(All50, ""));
        Assert.Equal("", own.Mailbox);
        Assert.Equal("own1", own.Email.Id);
        var a = Assert.Single(mi.Search(All50, "A@x.edu"));
        Assert.Equal("a@x.edu", a.Mailbox);
        Assert.Equal("a1", a.Email.Id);
    }

    [Fact]
    public void Search_UnknownMailbox_Throws()
    {
        var mi = Make(A, B);
        var ex = Assert.Throws<McpException>(() => mi.Search(All50, "x@y.org"));
        Assert.Equal(UnknownMsg, ex.Message);
    }

    private MailboxIndexes Seed16()
    {
        var mi = Make(A, B);
        Put(mi.For(""), Email("o10", T(10)), Email("o7", T(7)), Email("o4", T(4)), Email("o1", T(1)));
        Put(mi.For(A), Email("a9", T(9)), Email("a6", T(6)), Email("a3", T(3)));
        Put(mi.For(B), Email("b8", T(8)), Email("b5", T(5)), Email("b2", T(2)));
        return mi;
    }

    [Fact]
    public void Search_All_MergesByDateDesc()
    {
        var mi = Seed16();
        var hits = mi.Search(new SearchFilter { Limit = 50 }, "all");
        Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(T), hits.Select(h => h.Email.Date));
        Assert.Equal(["", A, B, "", A, B, "", A, B, ""], hits.Select(h => h.Mailbox));
    }

    [Theory]
    [InlineData(2, 3, new[] { 8, 7, 6 })]
    [InlineData(9, 5, new[] { 1 })]
    [InlineData(10, 5, new int[0])]
    [InlineData(0, 0, new int[0])]
    [InlineData(-5, 2, new[] { 10, 9 })]
    public void Search_All_Paging(int offset, int limit, int[] days)
    {
        var mi = Seed16();
        var hits = mi.Search(new SearchFilter { Offset = offset, Limit = limit }, "all");
        Assert.Equal(days.Select(T), hits.Select(h => h.Email.Date));
    }

    [Fact]
    public void Search_All_TieOwnFirst()
    {
        var mi = Make(A, B);
        Put(mi.For(B), Email("b", T(5)));
        Put(mi.For(A), Email("a", T(5)));
        Put(mi.For(""), Email("o", T(5)));
        var hits = mi.Search(All50, "all");
        Assert.Equal(["o", "a", "b"], hits.Select(h => h.Email.Id));
    }

    [Fact]
    public void Search_All_AppliesFilterInEachIndex()
    {
        var mi = Make(A);
        Put(mi.For(""), Email("o1", T(3), "Inbox"), Email("o2", T(2), "Sent Items"));
        Put(mi.For(A), Email("a1", T(1), "Inbox"), Email("a2", T(4), "Sent Items"));
        var hits = mi.Search(new SearchFilter { Folder = "inbox", Limit = 50 }, "all");
        Assert.Equal(["o1", "a1"], hits.Select(h => h.Email.Id));
    }

    [Fact]
    public void Search_All_DeepPagingAcrossChunks()
    {
        var mi = Make(A);
        var own = Enumerable.Range(0, 250).Select(i => Email("o" + i, Base.AddHours(2 * i))).ToList();
        var sh = Enumerable.Range(0, 250).Select(i => Email("a" + i, Base.AddHours(2 * i + 1))).ToList();
        for (var i = 0; i < 250; i += 50)
        {
            mi.For("").UpsertBatch(own.Skip(i).Take(50).ToList());
            mi.For(A).UpsertBatch(sh.Skip(i).Take(50).ToList());
        }
        var expected = own.Concat(sh).Select(e => e.EventTime).OrderByDescending(t => t).Skip(300).Take(100).ToList();
        var hits = mi.Search(new SearchFilter { Offset = 300, Limit = 100 }, "all");
        Assert.Equal(100, hits.Count);
        Assert.Equal(expected, hits.Select(h => h.Email.Date).ToList());
        for (var i = 1; i < hits.Count; i++) Assert.NotEqual(hits[i - 1].Mailbox, hits[i].Mailbox);
    }

    [Fact]
    public void Search_All_OwnOnly()
    {
        var mi = Make();
        Put(mi.For(""), Email("o1", T(1)), Email("o2", T(2)));
        Assert.Equal(mi.Search(All50, "").Select(h => h.Email.Id), mi.Search(All50, "all").Select(h => h.Email.Id));
    }

    [Fact]
    public void Search_All_ExcludesDisabled()
    {
        var mi = Make(A);
        Put(mi.For(""), Email("o1", T(1)));
        Put(mi.For(A), Email("a1", T(2)));
        mi.Disable(A);
        var hit = Assert.Single(mi.Search(All50, "all"));
        Assert.Equal("o1", hit.Email.Id);
    }

    // ---- Locate

    [Fact]
    public void Locate_FindsSharedRow()
    {
        var mi = Make(A);
        Put(mi.For(A), Email("a1", T(1)));
        var r = mi.Locate("a1");
        Assert.NotNull(r);
        Assert.Equal(A, r.Mailbox);
        Assert.Equal("a1", r.Email.EntryId);
    }

    [Fact]
    public void Locate_FindsOwnRow()
    {
        var mi = Make(A);
        Put(mi.For(""), Email("o1", T(1)));
        Assert.Equal("", mi.Locate("o1")!.Mailbox);
    }

    [Fact]
    public void Locate_DuplicateId_OwnWins()
    {
        var mi = Make(A);
        Put(mi.For(A), Email("d", T(1)));
        Put(mi.For(""), Email("d", T(1)));
        Assert.Equal("", mi.Locate("d")!.Mailbox);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Locate_Unknown_Null(string? id)
    {
        var mi = Make(A);
        Assert.Null(mi.Locate(id));
    }

    [Fact]
    public void Locate_DisabledMailbox_Null()
    {
        var mi = Make(A);
        Put(mi.For(A), Email("a1", T(1)));
        mi.Disable(A);
        Assert.Null(mi.Locate("a1"));
    }

    // ---- Add / Disable / Remove

    [Fact]
    public void Add_New_CreatesFileAndAppends()
    {
        var mi = Make(A);
        var ix = mi.Add("C@x.edu");
        Assert.True(File.Exists(Db(_dir, "index-c@x.edu.db")));
        Assert.Equal("c@x.edu", mi.Mailboxes[^1]);
        Assert.Same(ix, mi.For("c@x.edu"));
    }

    [Fact]
    public void Add_Idempotent()
    {
        var mi = Make();
        Assert.Same(mi.Add(A), mi.Add(A));
        Assert.Equal(["", A], mi.Mailboxes);
    }

    [Fact]
    public void Add_Own_ReturnsOwn()
    {
        var mi = Make(A);
        Assert.Same(mi.For(""), mi.Add(""));
        Assert.Equal(["", A], mi.Mailboxes);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("nope")]
    public void Add_Invalid_Throws(string bad)
    {
        var mi = Make();
        Assert.Throws<ArgumentException>(() => mi.Add(bad));
    }

    [Fact]
    public void Disable_KeepsFile_ForThrows()
    {
        var mi = Make(A);
        Assert.True(mi.Disable(A));
        Assert.True(File.Exists(Db(_dir, "index-a@x.edu.db")));
        Assert.Throws<McpException>(() => mi.For(A));
        Assert.Equal([""], mi.Mailboxes);
    }

    [Fact]
    public void Disable_Unknown_False()
    {
        var mi = Make();
        Assert.False(mi.Disable("x@y.org"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("all")]
    public void Disable_Own_Throws(string key)
    {
        var mi = Make();
        Assert.Throws<ArgumentException>(() => mi.Disable(key));
    }

    [Fact]
    public void Disable_ThenAdd_KeepsData()
    {
        var mi = Make(A);
        Put(mi.For(A), Email("a1", T(1)));
        mi.Disable(A);
        mi.Add(A);
        Assert.Equal(A, mi.Locate("a1")!.Mailbox);
    }

    [Fact]
    public void Remove_KeepFile()
    {
        var mi = Make(A);
        mi.Remove(A, false);
        Assert.True(File.Exists(Db(_dir, "index-a@x.edu.db")));
        Assert.DoesNotContain(A, mi.Mailboxes);
    }

    [Fact]
    public void Remove_DeleteFile()
    {
        var mi = Make(A);
        Put(mi.For(A), Email("a1", T(1)));
        mi.Remove(A, true);
        Assert.False(File.Exists(Db(_dir, "index-a@x.edu.db")));
        Assert.False(File.Exists(Db(_dir, "index-a@x.edu.db-wal")));
        Assert.False(File.Exists(Db(_dir, "index-a@x.edu.db-shm")));
        Assert.Empty(mi.Add(A).Search(All50));
    }

    [Fact]
    public void Remove_AfterDisable_DeletesFile()
    {
        var mi = Make(A);
        mi.Disable(A);
        mi.Remove(A, true);
        Assert.False(File.Exists(Db(_dir, "index-a@x.edu.db")));
    }

    [Fact]
    public void Remove_Unknown_NoThrow()
    {
        var mi = Make();
        mi.Remove("x@y.org", true);
    }

    [Fact]
    public void Remove_Own_Throws()
    {
        var mi = Make();
        Assert.Throws<ArgumentException>(() => mi.Remove("", true));
        Assert.Throws<ArgumentException>(() => mi.Remove("all", false));
        Assert.True(File.Exists(Db(_dir, "index.db")));
    }

    // ---- Dispose

    [Fact]
    public void Dispose_ReleasesFiles()
    {
        var mi = Make(A);
        Put(mi.For(""), Email("o1", T(1)));
        Put(mi.For(A), Email("a1", T(1)));
        mi.Dispose();
        File.Delete(Db(_dir, "index.db"));
        File.Delete(Db(_dir, "index-a@x.edu.db"));
        Assert.False(File.Exists(Db(_dir, "index.db")));
        Assert.False(File.Exists(Db(_dir, "index-a@x.edu.db")));
    }

    [Fact]
    public void Dispose_Twice_NoThrow()
    {
        var mi = Make();
        mi.Dispose();
        mi.Dispose();
        Assert.Throws<ObjectDisposedException>(() => mi.For(""));
    }

    [Fact]
    public void MailboxError_messages()
    {
        string[] keys = ["", "help@x.org"];
        Assert.Equal(
            "Mailbox \"Nobody@X.org\" is not configured or not enabled. Available mailboxes: \"\" (your own mailbox), \"help@x.org\". To use another shared mailbox, ask the user to enable it in the Clippy tray menu under Shared mailboxes.",
            MailboxIndexes.MailboxError(" Nobody@X.org ", keys).Message);
        Assert.Equal(
            "Mailbox \"all\" only works with search tools. Use one mailbox: \"\" (your own mailbox), \"help@x.org\".",
            MailboxIndexes.MailboxError(" ALL ", keys).Message);
    }
}
