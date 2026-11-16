using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexEditsTests : IDisposable
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-11-13T12:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-edits-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private string DbFile => Path.Combine(_dir, "index.db");

    public EmailIndexEditsTests() => _ix = new EmailIndex(DbFile);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private long Count(string table)
    {
        using var c = Raw();
        return c.ExecuteScalar<long>("SELECT COUNT(*) FROM " + table);
    }

    private void Sent(string id, DateTimeOffset at, string? marker = "ai", string? irt = "<A@x>", string folder = "Sent Items",
        string? body = "sent body")
    {
        _ix.UpsertBatch([new IndexedEmail
        {
            EntryId = id, Folder = folder, ClippyMarker = marker, InReplyTo = irt, EventTime = at, Body = body, Subject = "s",
        }]);
    }

    private static AiEdit Edit(string sentId, DateTimeOffset sent, double sim = 0.5, string? d = "draft", string? s = "sent") =>
        new(sentId, sent, sim, d?.Length ?? 10, s?.Length ?? 10, d, s);

    private void Pending(string id, string imid, DateTimeOffset created, string text = "text") =>
        _ix.RecordAiDraftText(id, "", imid, created, text);

    [Fact]
    public void Record_and_get_round_trip_with_trimmed_imid()
    {
        _ix.RecordAiDraftText("D1", "", " <A@x> ", T, "hello");
        Assert.Equal(new AiDraftText("D1", "", "<A@x>", T, "hello", AiDraftTextState.Pending, T), _ix.GetAiDraftText("D1"));
        Assert.Null(_ix.GetAiDraftText("nope"));
        Assert.Throws<ArgumentException>(() => _ix.GetAiDraftText(""));
    }

    [Fact]
    public void Record_validates_and_writes_nothing()
    {
        Assert.Throws<ArgumentException>(() => _ix.RecordAiDraftText(" ", "", "<A@x>", T, "t"));
        Assert.Throws<ArgumentException>(() => _ix.RecordAiDraftText("D1", "", "  ", T, "t"));
        Assert.Throws<ArgumentException>(() => _ix.RecordAiDraftText("D1", "", "<A@x>", T, new string('a', 1501)));
        Assert.Throws<ArgumentNullException>(() => _ix.RecordAiDraftText("D1", "", "<A@x>", T, null!));
        Assert.Throws<ArgumentNullException>(() => _ix.RecordAiDraftText("D1", null!, "<A@x>", T, "t"));
        Assert.Equal(0, Count("ai_draft_texts"));
        _ix.RecordAiDraftText("D1", "", "<A@x>", T, new string('a', 1500));
        Assert.Equal(1, Count("ai_draft_texts"));
    }

    [Fact]
    public void Recording_again_overwrites_every_column_and_resets_the_state()
    {
        _ix.RecordAiDraftText("D1", "", "<A@x>", T, "one");
        Pending("D2", "<A@x>", T.AddHours(-1));
        Assert.True(_ix.StoreEdit("D1", "<A@x>", Edit("S1", T.AddMinutes(5)), T.AddMinutes(6)));
        Assert.Equal(AiDraftTextState.Matched, _ix.GetAiDraftText("D1")!.State);

        _ix.RecordAiDraftText("D1", "helpdesk@example.org", "<B@x>", T.AddHours(1), "two");

        Assert.Equal(new AiDraftText("D1", "helpdesk@example.org", "<B@x>", T.AddHours(1), "two", AiDraftTextState.Pending, T.AddHours(1)),
            _ix.GetAiDraftText("D1"));
        Assert.Equal(2, Count("ai_draft_texts"));
    }

    [Fact]
    public void SentAiRepliesToMatch_lists_only_matching_rows_newest_first()
    {
        Pending("D1", "<A@x>", T.AddHours(-1));
        Sent("S1", T);
        Sent("S5", T.AddMinutes(-10), folder: "Sent Items\\Archive");
        Sent("S2", T, marker: "tool");
        Sent("S3", T, marker: null);
        Sent("S4", T, irt: null);
        Sent("S6", T, folder: "Inbox");
        Sent("S7", T.AddDays(-40));
        Sent("S8", T, irt: "<B@x>");
        Pending("DB", "<B@x>", T.AddMinutes(10));
        Sent("S9", T.AddMinutes(-20));
        using (var c = Raw())
            c.Execute("INSERT INTO ai_edits (sent_entry_id, sent, similarity, draft_chars, sent_chars) VALUES ('S9', 1, 0.5, 1, 1)");
        Sent("S10", T, irt: "<C@x>");
        Pending("DC", "<C@x>", T.AddHours(-1));
        using (var c = Raw())
            c.Execute("UPDATE ai_draft_texts SET state = 'superseded' WHERE draft_entry_id = 'DC'");
        Sent("S11", T, irt: "<a@x>");

        var rows = _ix.SentAiRepliesToMatch(T.AddDays(-30), 100);

        Assert.Equal(["S1", "S5"], rows.Select(r => r.EntryId));
        Assert.Equal("<A@x>", rows[0].InReplyTo);
        Assert.Equal(T, rows[0].EventTime);
        Assert.Equal("sent body", rows[0].Body);
        Assert.Single(_ix.SentAiRepliesToMatch(T.AddDays(-30), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SentAiRepliesToMatch(T, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SentAiRepliesToMatch(T, 501));
    }

    [Fact]
    public void PendingDraftTexts_newest_first_and_filtered()
    {
        Pending("D1", "<A@x>", T.AddHours(-3));
        Pending("D2", "<A@x>", T.AddHours(-1));
        Pending("D3", "<A@x>", T.AddHours(1));
        Pending("D4", "<B@x>", T.AddHours(-1));
        Pending("D5", "<A@x>", T.AddHours(-2));
        Assert.True(_ix.StoreEdit("D5", "<ZZ@x>", Edit("SQ", T.AddHours(-2)), T));

        var rows = _ix.PendingDraftTexts(" <A@x> ", T);

        Assert.Equal(["D2", "D1"], rows.Select(r => r.DraftEntryId));
        Assert.Empty(_ix.PendingDraftTexts(null, T));
        Assert.Empty(_ix.PendingDraftTexts("  ", T));
    }

    [Fact]
    public void StoreEdit_matches_one_supersedes_older_and_keeps_newer()
    {
        Pending("D1", "<A@x>", T.AddHours(-3), "one");
        Pending("D2", "<A@x>", T.AddHours(-2), "two");
        Pending("D3", "<A@x>", T.AddHours(1), "three");
        Pending("D4", "<B@x>", T.AddHours(-3), "four");
        var edit = new AiEdit("S1", T, 0.75, 3, 4, "two", "sent");

        Assert.True(_ix.StoreEdit("D2", "<A@x>", edit, T.AddMinutes(1)));

        var d2 = _ix.GetAiDraftText("D2")!;
        Assert.Equal((AiDraftTextState.Matched, ""), (d2.State, d2.Text));
        Assert.Equal(T.AddMinutes(1), d2.Updated);
        var d1 = _ix.GetAiDraftText("D1")!;
        Assert.Equal((AiDraftTextState.Superseded, ""), (d1.State, d1.Text));
        Assert.Equal((AiDraftTextState.Pending, "three"), (_ix.GetAiDraftText("D3")!.State, _ix.GetAiDraftText("D3")!.Text));
        Assert.Equal((AiDraftTextState.Pending, "four"), (_ix.GetAiDraftText("D4")!.State, _ix.GetAiDraftText("D4")!.Text));
        Assert.Equal(edit, Assert.Single(_ix.AiEdits(10)));
    }

    [Fact]
    public void StoreEdit_twice_for_the_same_sent_row_or_a_non_pending_match_is_refused()
    {
        Pending("D2", "<A@x>", T.AddHours(-2));
        Pending("D3", "<A@x>", T.AddHours(1));
        Assert.True(_ix.StoreEdit("D2", "<A@x>", Edit("S1", T), T));

        Assert.False(_ix.StoreEdit("D3", "<A@x>", Edit("S1", T), T));
        Assert.Equal(AiDraftTextState.Pending, _ix.GetAiDraftText("D3")!.State);
        Assert.Equal(1, Count("ai_edits"));

        Assert.False(_ix.StoreEdit("D2", "<A@x>", Edit("S2", T), T));   // D2 is matched already
        Assert.Equal(1, Count("ai_edits"));
    }

    [Fact]
    public void Only_the_newest_twenty_edits_keep_their_texts()
    {
        for (var i = 0; i < 22; i++)
        {
            Pending("D" + i, "<A" + i + "@x>", T.AddHours(-1));
            Assert.True(_ix.StoreEdit("D" + i, "<A" + i + "@x>", Edit("S" + i, T.AddMinutes(i), 0.4, "d" + i, "s" + i), T));
        }
        var rows = _ix.AiEdits(100);
        Assert.Equal(22, rows.Count);
        Assert.Equal("S21", rows[0].SentEntryId);
        Assert.All(rows.Take(20), r => Assert.NotNull(r.DraftText));
        Assert.All(rows.Skip(20), r =>
        {
            Assert.Null(r.DraftText);
            Assert.Null(r.SentText);
            Assert.Equal(0.4, r.Similarity);
            Assert.Equal(2, r.DraftChars);
        });

        Pending("DN", "<N@x>", T.AddHours(-1));
        Assert.True(_ix.StoreEdit("DN", "<N@x>", Edit("SN", T.AddMinutes(50), 1.0, null, null), T));
        Assert.Equal(23, Count("ai_edits"));
        Assert.Equal(20, _ix.AiEdits(100).Count(r => r.DraftText is not null));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.AiEdits(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.AiEdits(1001));
    }

    [Fact]
    public void StoreEdit_validates_and_writes_nothing()
    {
        Pending("D1", "<A@x>", T.AddHours(-1));
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, 1.1, -0.1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => _ix.StoreEdit("D1", "<A@x>", Edit("S1", T, bad), T));
        Assert.Throws<ArgumentException>(() => _ix.StoreEdit("D1", "<A@x>", Edit("S1", T, 0.5, "d", null), T));
        Assert.Throws<ArgumentException>(() => _ix.StoreEdit("D1", "<A@x>", Edit("S1", T, 0.5, new string('a', 1501), "s"), T));
        Assert.Throws<ArgumentException>(() => _ix.StoreEdit(" ", "<A@x>", Edit("S1", T), T));
        Assert.Throws<ArgumentException>(() => _ix.StoreEdit("D1", " ", Edit("S1", T), T));
        Assert.Throws<ArgumentException>(() => _ix.StoreEdit("D1", "<A@x>", Edit(" ", T), T));
        Assert.Throws<ArgumentException>(() => _ix.StoreEdit("D1", "<A@x>", new AiEdit("S1", T, 0.5, -1, 0, null, null), T));
        Assert.Throws<ArgumentNullException>(() => _ix.StoreEdit("D1", "<A@x>", null!, T));
        Assert.Equal(0, Count("ai_edits"));
        Assert.Equal(AiDraftTextState.Pending, _ix.GetAiDraftText("D1")!.State);
    }

    [Fact]
    public void PurgeDecisions_expires_and_deletes_per_the_retention_rules()
    {
        var now = T;
        Pending("P31", "<1@x>", now.AddDays(-31));
        Pending("P29", "<2@x>", now.AddDays(-29));
        Pending("E31", "<3@x>", now.AddDays(-80));
        Pending("M31", "<4@x>", now.AddDays(-40));
        Pending("S29", "<5@x>", now.AddDays(-40));
        using (var c = Raw())
        {
            c.Execute("UPDATE ai_draft_texts SET state = 'expired', text = '', updated = @u WHERE draft_entry_id = 'E31'", new { u = now.AddDays(-31).ToUnixTimeSeconds() });
            c.Execute("UPDATE ai_draft_texts SET state = 'matched', text = '', updated = @u WHERE draft_entry_id = 'M31'", new { u = now.AddDays(-31).ToUnixTimeSeconds() });
            c.Execute("UPDATE ai_draft_texts SET state = 'superseded', text = '', updated = @u WHERE draft_entry_id = 'S29'", new { u = now.AddDays(-29).ToUnixTimeSeconds() });
            c.Execute("INSERT INTO ai_edits (sent_entry_id, sent, similarity, draft_chars, sent_chars) VALUES ('O91', @a, 0.5, 1, 1), ('O89', @b, 0.5, 1, 1)",
                new { a = now.AddDays(-91).ToUnixTimeSeconds(), b = now.AddDays(-89).ToUnixTimeSeconds() });
        }

        var deleted = _ix.PurgeDecisions(now);

        Assert.Equal(3, deleted);
        var p31 = _ix.GetAiDraftText("P31")!;
        Assert.Equal((AiDraftTextState.Expired, "", now), (p31.State, p31.Text, p31.Updated));
        Assert.Equal(AiDraftTextState.Pending, _ix.GetAiDraftText("P29")!.State);
        Assert.Null(_ix.GetAiDraftText("E31"));
        Assert.Null(_ix.GetAiDraftText("M31"));
        Assert.NotNull(_ix.GetAiDraftText("S29"));
        Assert.Equal(["O89"], _ix.AiEdits(10).Select(e => e.SentEntryId));
    }
}
