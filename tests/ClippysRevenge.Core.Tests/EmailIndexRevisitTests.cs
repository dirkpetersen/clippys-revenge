using ClippysRevenge.Core;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexRevisitTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z");
    private static readonly string FP = new('a', 64);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-rev-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private string DbFile => Path.Combine(_dir, "index.db");

    public EmailIndexRevisitTests() => _ix = new EmailIndex(DbFile);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static DateTimeOffset T(int minutes) => Now.AddMinutes(minutes);

    private static IndexedEmail Mail(string id, int at, string folder = "Inbox", string? topic = "Ski", string? imid = null) => new()
    {
        EntryId = id,
        StoreId = "store-" + id,
        Folder = folder,
        Subject = "subj " + id,
        SenderName = "Sender " + id,
        SenderEmail = id + "@x.org",
        To = "to@x.org",
        Cc = "cc@x.org",
        ConversationTopic = topic,
        InternetMessageId = imid,
        EventTime = T(at),
        Attachments = [],
    };

    private void Rec(string draftId, string sourceId, int created, int checkedAt) =>
        _ix.RecordAiDraft(draftId, sourceId, T(created), FP, T(checkedAt), Now);

    private long Count(string table)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = false }.ToString());
        c.Open();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table);
    }

    // ---- RecordAiDraft / GetAiDraft ----

    [Fact]
    public void Record_and_get_round_trip()
    {
        _ix.RecordAiDraft("D", "S", T(1), FP, T(0), Now);
        var r = _ix.GetAiDraft("D")!;
        Assert.Equal("D", r.DraftEntryId);
        Assert.Equal("S", r.SourceEntryId);
        Assert.Equal(T(1), r.Created);
        Assert.Equal(FP, r.Fingerprint);
        Assert.Equal(T(0), r.Checked);
        Assert.Equal(AiDraftState.Open, r.State);
        Assert.Equal(0, r.Attempts);
        Assert.Equal(Now, r.Updated);
        Assert.Null(_ix.GetAiDraft("nope"));
        Assert.Throws<ArgumentException>(() => _ix.GetAiDraft(""));
    }

    [Theory]
    [InlineData("", "S", "a", "AI draft: DraftEntryId is empty.")]
    [InlineData(" ", "S", "a", "AI draft: DraftEntryId is empty.")]
    [InlineData("D", "", "a", "AI draft: SourceEntryId is empty.")]
    [InlineData("D", " ", "a", "AI draft: SourceEntryId is empty.")]
    [InlineData("D", "S", "abc", "AI draft: the fingerprint must be 64 lower-case hex characters.")]
    [InlineData("D", "S", "A", "AI draft: the fingerprint must be 64 lower-case hex characters.")]
    public void Record_validation(string draftId, string sourceId, string fpKind, string message)
    {
        var fp = fpKind == "A" ? new string('A', 64) : fpKind == "a" ? FP : fpKind;
        var ex = Assert.Throws<ArgumentException>(() => _ix.RecordAiDraft(draftId, sourceId, T(1), fp, T(0), Now));
        Assert.StartsWith(message, ex.Message);
        Assert.Equal(0, Count("ai_drafts"));
    }

    [Fact]
    public void Record_twice_overwrites_and_reopens()
    {
        _ix.RecordAiDraft("D", "S", T(1), FP, T(0), Now);
        Assert.True(_ix.SetAiDraftState("D", AiDraftState.Edited, null, 0, Now));
        var fp2 = new string('b', 64);
        _ix.RecordAiDraft("D", "S2", T(3), fp2, T(2), Now.AddMinutes(1));
        Assert.Equal(1, Count("ai_drafts"));
        var r = _ix.GetAiDraft("D")!;
        Assert.Equal("S2", r.SourceEntryId);
        Assert.Equal(fp2, r.Fingerprint);
        Assert.Equal(T(3), r.Created);
        Assert.Equal(T(2), r.Checked);
        Assert.Equal(AiDraftState.Open, r.State);
        Assert.Equal(Now.AddMinutes(1), r.Updated);
    }

    [Fact]
    public void State_and_outcome_ids()
    {
        string[] states = ["open", "replaced", "removed", "edited", "replied", "gone", "left"];
        for (var i = 0; i < states.Length; i++)
        {
            var s = (AiDraftState)i;
            Assert.Equal(states[i], AiDraftStates.ToDb(s));
            Assert.Equal(s, AiDraftStates.FromDb(states[i]));
        }
        Assert.Equal(AiDraftState.Left, AiDraftStates.FromDb(null));
        Assert.Equal(AiDraftState.Left, AiDraftStates.FromDb("Open"));
        Assert.Equal(AiDraftState.Left, AiDraftStates.FromDb("x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiDraftStates.ToDb((AiDraftState)99));
        string[] outcomes = ["keep", "replace", "remove", "low_confidence", "failed"];
        for (var i = 0; i < outcomes.Length; i++)
            Assert.Equal(outcomes[i], AiRevisitOutcomes.ToDb((AiRevisitOutcome)i));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiRevisitOutcomes.ToDb((AiRevisitOutcome)99));
    }

    // ---- SetAiDraftState ----

    [Fact]
    public void Set_state_only_changes_open_rows()
    {
        Rec("D", "S", 1, 0);
        Assert.True(_ix.SetAiDraftState("D", AiDraftState.Edited, null, 0, Now));
        Assert.False(_ix.SetAiDraftState("D", AiDraftState.Open, null, 0, Now));
        Assert.Equal(AiDraftState.Edited, _ix.GetAiDraft("D")!.State);
        Assert.False(_ix.SetAiDraftState("unknown", AiDraftState.Gone, null, 0, Now));
    }

    [Fact]
    public void Set_state_checked_never_moves_back()
    {
        Rec("D", "S", 1, 0);
        Assert.True(_ix.SetAiDraftState("D", AiDraftState.Open, T(10), 1, Now.AddMinutes(1)));
        var r = _ix.GetAiDraft("D")!;
        Assert.Equal(T(10), r.Checked);
        Assert.Equal(1, r.Attempts);
        Assert.Equal(Now.AddMinutes(1), r.Updated);
        Assert.True(_ix.SetAiDraftState("D", AiDraftState.Open, T(5), 2, Now.AddMinutes(2)));
        r = _ix.GetAiDraft("D")!;
        Assert.Equal(T(10), r.Checked);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(Now.AddMinutes(2), r.Updated);
        Assert.True(_ix.SetAiDraftState("D", AiDraftState.Open, null, 0, Now.AddMinutes(3)));
        r = _ix.GetAiDraft("D")!;
        Assert.Equal(T(10), r.Checked);
        Assert.Equal(0, r.Attempts);
        Assert.Equal(Now.AddMinutes(3), r.Updated);
    }

    [Fact]
    public void Set_state_arguments()
    {
        Rec("D", "S", 1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SetAiDraftState("D", AiDraftState.Open, null, -1, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SetAiDraftState("D", (AiDraftState)99, null, 0, Now));
        Assert.Throws<ArgumentException>(() => _ix.SetAiDraftState("", AiDraftState.Open, null, 0, Now));
        Assert.Equal(AiDraftState.Open, _ix.GetAiDraft("D")!.State);
    }

    // ---- ThreadInbox ----

    [Fact]
    public void ThreadInbox_filters_orders_and_maps()
    {
        _ix.UpsertBatch([
            Mail("i1", 1), Mail("i2", 2, "Inbox\\Trips"), Mail("s3", 3, "Sent Items"), Mail("a4", 4, "Archive"),
            Mail("i5", 5, "inbox"), Mail("o6", 6, "InboxOld"), Mail("i7", 7, topic: "SKI "), Mail("x8", 8, topic: "Other"),
        ]);
        Assert.Equal(["i7", "i5", "i2", "i1"], _ix.ThreadInbox("ski", T(0), T(10), 100).Select(r => r.EntryId));
        Assert.Equal(["i5", "i2"], _ix.ThreadInbox("ski", T(1), T(5), 100).Select(r => r.EntryId));
        Assert.Equal(["i7"], _ix.ThreadInbox("ski", T(0), T(10), 1).Select(r => r.EntryId));
        Assert.Empty(_ix.ThreadInbox(null, T(0), T(10), 10));
        Assert.Empty(_ix.ThreadInbox("  ", T(0), T(10), 10));
        Assert.Empty(_ix.ThreadInbox("ski", T(10), T(10), 10));
        Assert.Empty(_ix.ThreadInbox("ski", T(11), T(10), 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.ThreadInbox("ski", T(0), T(10), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.ThreadInbox("ski", T(0), T(10), 101));
        var c = _ix.ThreadInbox("ski", T(6), T(7), 5).Single();
        Assert.Equal("i7", c.EntryId);
        Assert.Equal("store-i7", c.StoreId);
        Assert.Equal("Sender i7", c.SenderName);
        Assert.Equal("i7@x.org", c.SenderEmail);
        Assert.Equal("to@x.org", c.To);
        Assert.Equal("cc@x.org", c.Cc);
        Assert.Equal("subj i7", c.Subject);
        Assert.Equal("SKI", c.ConversationTopic);
        Assert.Equal(T(7), c.EventTime);
    }

    // ---- DueRevisits ----

    private void SourceAndDraft(string draftId = "D", string sourceId = "S", string topic = "Ski", int sourceAt = 0, int created = 1,
        int checkedAt = 0)
    {
        _ix.UpsertBatch([Mail(sourceId, sourceAt, topic: topic, imid: "<" + sourceId + "@x>")]);
        Rec(draftId, sourceId, created, checkedAt);
    }

    [Fact]
    public void Due_basic()
    {
        SourceAndDraft();
        Assert.Empty(_ix.DueRevisits(T(-100), 10));
        _ix.UpsertBatch([Mail("N", 5)]);
        var due = Assert.Single(_ix.DueRevisits(T(-100), 10));
        Assert.Equal(T(5), due.UpTo);
        Assert.Equal("Ski", due.Thread.ConversationTopic);
        Assert.Equal("<S@x>", due.Thread.SourceImid);
        Assert.Equal(T(0), due.Thread.SourceTime);
        Assert.Equal("D", due.Thread.Draft.DraftEntryId);
    }

    [Fact]
    public void Due_only_inbox_or_under_counts()
    {
        SourceAndDraft();
        _ix.UpsertBatch([Mail("sent", 4, "Sent Items"), Mail("arch", 5, "Archive")]);
        Assert.Empty(_ix.DueRevisits(T(-100), 10));
        _ix.UpsertBatch([Mail("sub", 6, "Inbox\\Sub")]);
        Assert.Equal(T(6), Assert.Single(_ix.DueRevisits(T(-100), 10)).UpTo);
    }

    [Fact]
    public void Due_upto_is_max_and_checked_is_exclusive()
    {
        SourceAndDraft();
        _ix.UpsertBatch([Mail("eq", 0, "Inbox\\Other"), Mail("a", 3), Mail("b", 7)]);
        Assert.Equal(T(7), Assert.Single(_ix.DueRevisits(T(-100), 10)).UpTo);
        _ix.SetAiDraftState("D", AiDraftState.Open, T(7), 0, Now);
        Assert.Empty(_ix.DueRevisits(T(-100), 10));
    }

    [Fact]
    public void Due_only_newest_draft_per_thread()
    {
        _ix.UpsertBatch([Mail("S1", 0, topic: "Ski"), Mail("S2", 0, topic: "SKI")]);
        Rec("D1", "S1", 1, 0);
        Rec("D2", "S2", 2, 6);
        _ix.UpsertBatch([Mail("n", 4)]);
        Assert.Empty(_ix.DueRevisits(T(-100), 10));
        _ix.UpsertBatch([Mail("m", 8)]);
        var due = Assert.Single(_ix.DueRevisits(T(-100), 10));
        Assert.Equal("D2", due.Thread.Draft.DraftEntryId);
        Assert.Equal(T(8), due.UpTo);
    }

    [Fact]
    public void Due_window_and_state()
    {
        SourceAndDraft();
        _ix.UpsertBatch([Mail("n", 5)]);
        Assert.Empty(_ix.DueRevisits(T(2), 10));
        Assert.Single(_ix.DueRevisits(T(1), 10));

        _ix.UpsertBatch([Mail("S2", 0)]);
        Rec("D2", "S2", 3, 0);
        Assert.Equal("D2", Assert.Single(_ix.DueRevisits(T(-100), 10)).Thread.Draft.DraftEntryId);
        _ix.SetAiDraftState("D2", AiDraftState.Edited, null, 0, Now);
        Assert.Equal("D", Assert.Single(_ix.DueRevisits(T(-100), 10)).Thread.Draft.DraftEntryId);
    }

    [Fact]
    public void Due_ignores_missing_source_or_topic()
    {
        Rec("D", "missing", 1, 0);
        _ix.UpsertBatch([Mail("S2", 0, topic: null), Mail("n", 5)]);
        Rec("D2", "S2", 1, 0);
        Assert.Empty(_ix.DueRevisits(T(-100), 10));
    }

    [Fact]
    public void Due_order_and_limit()
    {
        _ix.UpsertBatch([Mail("Sa", 0, topic: "A"), Mail("Sb", 0, topic: "B"), Mail("Sc", 0, topic: "C"),
            Mail("na", 5, topic: "A"), Mail("nb", 9, topic: "B"), Mail("nc", 7, topic: "C")]);
        Rec("Da", "Sa", 1, 0);
        Rec("Db", "Sb", 1, 0);
        Rec("Dc", "Sc", 1, 0);
        Assert.Equal(["Db", "Dc", "Da"], _ix.DueRevisits(T(-100), 10).Select(d => d.Thread.Draft.DraftEntryId));
        Assert.Equal(["Db", "Dc"], _ix.DueRevisits(T(-100), 2).Select(d => d.Thread.Draft.DraftEntryId));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.DueRevisits(T(-100), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.DueRevisits(T(-100), 101));
    }

    // ---- OpenAiDraftForThread ----

    [Fact]
    public void OpenAiDraftForThread_picks_newest_with_earlier_source()
    {
        _ix.UpsertBatch([Mail("S1", 0), Mail("S2", 4)]);
        Rec("D1", "S1", 1, 0);
        Rec("D2", "S2", 5, 4);
        Assert.Equal("D2", _ix.OpenAiDraftForThread("Ski", T(10))!.Draft.DraftEntryId);
        Assert.Equal("D1", _ix.OpenAiDraftForThread("Ski", T(4))!.Draft.DraftEntryId);
        Assert.Null(_ix.OpenAiDraftForThread("Ski", T(0)));
        Assert.Equal("D2", _ix.OpenAiDraftForThread("SKI ", T(10))!.Draft.DraftEntryId);
        _ix.SetAiDraftState("D2", AiDraftState.Edited, null, 0, Now);
        Assert.Equal("D1", _ix.OpenAiDraftForThread("Ski", T(10))!.Draft.DraftEntryId);
        Assert.Null(_ix.OpenAiDraftForThread(null, T(10)));
        Assert.Null(_ix.OpenAiDraftForThread("  ", T(10)));
    }

    // ---- AddRevisit ----

    [Fact]
    public void AddRevisit_inserts_and_validates()
    {
        _ix.AddRevisit("D", T(1), AiRevisitOutcome.Keep, 1);
        _ix.AddRevisit("D", T(2), AiRevisitOutcome.Failed, 0);
        Assert.Equal(2, Count("ai_revisits"));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.AddRevisit("D", T(1), AiRevisitOutcome.Keep, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.AddRevisit("D", T(1), (AiRevisitOutcome)99, 1));
        Assert.Throws<ArgumentException>(() => _ix.AddRevisit("", T(1), AiRevisitOutcome.Keep, 1));
        Assert.Equal(2, Count("ai_revisits"));
    }
}
