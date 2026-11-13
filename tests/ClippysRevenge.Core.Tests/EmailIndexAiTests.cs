using ClippysRevenge.Core;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class EmailIndexAiTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-ai-" + Guid.NewGuid().ToString("N"));
    private readonly EmailIndex _ix;
    private string DbFile => Path.Combine(_dir, "index.db");

    public EmailIndexAiTests() => _ix = new EmailIndex(DbFile);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IndexedEmail Mail(string id, DateTimeOffset? at = null, string folder = "Inbox", string? subject = "s",
        string? body = null, string? irt = null, string? topic = null, string? marker = null, DateTimeOffset? lm = null,
        bool unread = false, string? imid = null, IReadOnlyList<AttachmentInfo>? atts = null) => new()
    {
        EntryId = id,
        Folder = folder,
        Subject = subject,
        Body = body,
        InReplyTo = irt,
        ConversationTopic = topic,
        ClippyMarker = marker,
        InternetMessageId = imid,
        EventTime = at ?? Now,
        LastModified = lm,
        Unread = unread,
        Attachments = atts ?? [],
    };

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbFile, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static DateTimeOffset T(int minutes) => Now.AddMinutes(minutes);

    [Fact]
    public void Upsert_round_trips_thread_columns_trimmed_and_normalized()
    {
        _ix.UpsertBatch([Mail("A", irt: " <x@y> ", topic: " Budget ", marker: "AI")]);
        var d = _ix.Get("A")!;
        Assert.Equal("<x@y>", d.InReplyTo);
        Assert.Equal("Budget", d.ConversationTopic);
        Assert.Equal("ai", d.ClippyMarker);
    }

    [Theory]
    [InlineData("tool", "tool")]
    [InlineData("x", null)]
    [InlineData("", null)]
    [InlineData(" ai", null)]
    public void Marker_is_normalized(string input, string? expected)
    {
        _ix.UpsertBatch([Mail("A", marker: input)]);
        Assert.Equal(expected, _ix.Get("A")!.ClippyMarker);
    }

    [Fact]
    public void Upsert_with_null_thread_values_keeps_stored_ones()
    {
        _ix.UpsertBatch([Mail("A", subject: "old", irt: "<a>", topic: "T", marker: "tool")]);
        _ix.UpsertBatch([Mail("A", subject: "new")]);
        var d = _ix.Get("A")!;
        Assert.Equal("new", d.Subject);
        Assert.Equal("<a>", d.InReplyTo);
        Assert.Equal("T", d.ConversationTopic);
        Assert.Equal("tool", d.ClippyMarker);
    }

    [Fact]
    public void Upsert_keeps_rowid()
    {
        _ix.UpsertBatch([Mail("A"), Mail("B")]);
        var a = _ix.Get("A")!.RowId;
        _ix.UpsertBatch([Mail("A", irt: "<z>", topic: "T")]);
        Assert.Equal(a, _ix.Get("A")!.RowId);
    }

    [Fact]
    public void SetThreadMeta_updates_only_the_three_columns()
    {
        var lm = T(-5);
        _ix.UpsertBatch([Mail("A", at: T(-10), lm: lm, unread: true, body: "bodyA"), Mail("B", at: T(-20), lm: lm, body: "bodyB")]);
        var n = _ix.SetThreadMeta([("A", "<a>", "T", "tool"), ("B", null, "T2", null)]);
        Assert.Equal(2, n);
        var a = _ix.Get("A")!;
        Assert.Equal(("<a>", "T", "tool"), (a.InReplyTo, a.ConversationTopic, a.ClippyMarker));
        var b = _ix.Get("B")!;
        Assert.Equal((null, "T2", null), (b.InReplyTo, b.ConversationTopic, b.ClippyMarker));
        Assert.Equal(lm.ToUnixTimeSeconds(), a.LastModified!.Value.ToUnixTimeSeconds());
        Assert.True(a.Unread);
        Assert.Equal(T(-10).ToUnixTimeSeconds(), a.EventTime.ToUnixTimeSeconds());
        Assert.Equal("bodyA", a.Body);
        using var c = Raw();
        c.Execute("INSERT INTO emails_fts(emails_fts, rank) VALUES('integrity-check', 1)");
    }

    [Fact]
    public void SetThreadMeta_never_clears()
    {
        _ix.UpsertBatch([Mail("A", marker: "ai", irt: "<a>", topic: "T")]);
        Assert.Equal(1, _ix.SetThreadMeta([("A", null, null, null)]));
        var d = _ix.Get("A")!;
        Assert.Equal(("<a>", "T", "ai"), (d.InReplyTo, d.ConversationTopic, d.ClippyMarker));
    }

    [Fact]
    public void SetThreadMeta_ignores_unknown_ids_and_never_inserts()
    {
        _ix.UpsertBatch([Mail("A")]);
        var n = _ix.SetThreadMeta([("NOPE", "<a>", "T", null), ("", "<a>", "T", null), (null!, "<a>", "T", null)]);
        Assert.Equal(0, n);
        Assert.Equal(1, _ix.Status().TotalEmails);
        Assert.Throws<ArgumentNullException>(() => _ix.SetThreadMeta(null!));
    }

    [Fact]
    public void SetThreadMeta_matches_exact_then_upper_case()
    {
        _ix.UpsertBatch([Mail("ABCD"), Mail("wxyz")]);
        Assert.Equal(1, _ix.SetThreadMeta([("abcd", null, "Upper", null)]));
        Assert.Equal("Upper", _ix.Get("ABCD")!.ConversationTopic);
        Assert.Equal(1, _ix.SetThreadMeta([("wxyz", null, "Exact", null)]));
        Assert.Equal("Exact", _ix.Get("wxyz")!.ConversationTopic);
    }

    private void SeedThread()
    {
        _ix.UpsertBatch([
            Mail("T1", at: T(-50), topic: "Budget", subject: "one"),
            Mail("T2", at: T(-40), topic: "BUDGET", folder: "Sent Items", subject: "two"),
            Mail("T3", at: T(-30), topic: "Budget", subject: "three"),
            Mail("T4", at: T(-20), topic: "Budget", subject: "four"),
            Mail("T5", at: T(-10), topic: "Budget", subject: "five"),
            Mail("O1", at: T(-30), topic: "Other"),
        ]);
    }

    [Fact]
    public void Thread_returns_matching_rows_newest_first()
    {
        SeedThread();
        var all = _ix.Thread("budget", T(-20), 8);
        Assert.Equal(["T4", "T3", "T2", "T1"], all.Select(m => m.EntryId));
        Assert.Equal(["T4", "T3"], _ix.Thread(" budget ", T(-20), 2).Select(m => m.EntryId));
        Assert.Equal(["T4", "T2", "T1"], _ix.Thread("budget", T(-20), 8, excludeEntryId: "T3").Select(m => m.EntryId));
        Assert.Equal("Sent Items", all[2].Folder);
        foreach (var t in new string?[] { null, "", "  " })
            Assert.Empty(_ix.Thread(t, T(0), 8));
    }

    [Fact]
    public void Thread_body_is_cut_and_attachment_names_split()
    {
        _ix.UpsertBatch([
            Mail("A", topic: "T", body: new string('x', 30_000),
                atts: [new AttachmentInfo(0, "a.pdf", 1, false), new AttachmentInfo(1, "b.png", 1, false)]),
            Mail("B", topic: "T", at: T(-1)),
        ]);
        var rows = _ix.Thread("T", T(1), 10);
        Assert.Equal(20_000, rows[0].Body!.Length);
        Assert.Equal(["a.pdf", "b.png"], rows[0].AttachmentNames);
        Assert.Empty(rows[1].AttachmentNames);
    }

    [Fact]
    public void Thread_max_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.Thread("T", Now, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.Thread("T", Now, 101));
        _ix.Thread("T", Now, 1);
        _ix.Thread("T", Now, 100);
    }

    [Fact]
    public void RepliedTo_by_imid()
    {
        _ix.UpsertBatch([
            Mail("S1", folder: "Sent Items", irt: "<m1>"),
            Mail("I1", folder: "Inbox", irt: "<m3>"),
            Mail("S2", folder: "Sent Items\\Archive", irt: "<m4>"),
        ]);
        Assert.True(_ix.RepliedTo("<m1>", null, T(1000)));
        Assert.True(_ix.RepliedTo(" <m1> ", null, T(1000)));
        Assert.False(_ix.RepliedTo("<m2>", null, T(-1000)));
        Assert.False(_ix.RepliedTo("<m3>", null, T(-1000)));
        Assert.True(_ix.RepliedTo("<m4>", null, T(-1000)));
    }

    [Fact]
    public void RepliedTo_by_topic_is_exclusive_of_after()
    {
        _ix.UpsertBatch([Mail("S1", folder: "Sent Items", topic: "Budget", at: T(-5))]);
        Assert.True(_ix.RepliedTo(null, "budget", T(-5).AddSeconds(-1)));
        Assert.False(_ix.RepliedTo(null, "budget", T(-5)));
        Assert.False(_ix.RepliedTo(null, null, T(-100)));
        Assert.False(_ix.RepliedTo(" ", "  ", T(-100)));
    }

    [Fact]
    public void SentForStyle_filters_markers_and_ai_originated()
    {
        _ix.UpsertBatch([
            Mail("plain", folder: "Sent Items", at: T(-10), body: new string('b', 25_000)),
            Mail("tool", folder: "Sent Items", at: T(-20), marker: "tool"),
            Mail("ai", folder: "Sent Items", at: T(-30), marker: "ai"),
            Mail("aiorig", folder: "Sent Items", at: T(-40), irt: "<src>"),
            Mail("kept", folder: "Sent Items", at: T(-50), irt: "<src2>"),
            Mail("inbox", folder: "Inbox", at: T(-15)),
            Mail("sub", folder: "Sent Items\\Old", at: T(-60)),
        ]);
        _ix.UpsertDecision(new AiDecision("x1", "<src>", null, T(-100), AiOutcome.Drafted, DraftCreated: T(-45)));
        _ix.UpsertDecision(new AiDecision("x2", "<src2>", null, T(-100), AiOutcome.Drafted, DraftCreated: T(-45)));
        var rows = _ix.SentForStyle(T(-100), T(0));
        Assert.Equal(["plain", "kept", "sub"], rows.Select(r => r.EntryId).Where(i => i != "aiorig"));
        Assert.DoesNotContain(rows, r => r.EntryId is "tool" or "ai" or "inbox" or "aiorig");
        Assert.Equal(20_000, rows[0].Body!.Length);
        // draft_created after the sent row: the row is kept
        _ix.UpsertDecision(new AiDecision("x1", "<src>", null, T(-100), AiOutcome.Drafted, DraftCreated: T(-39)));
        Assert.Contains(_ix.SentForStyle(T(-100), T(0)), r => r.EntryId == "aiorig");
        Assert.Single(_ix.SentForStyle(T(-100), T(0), 1));
        Assert.Empty(_ix.SentForStyle(T(0), T(-100)));
        // inclusive bounds
        Assert.Equal(["plain"], _ix.SentForStyle(T(-10), T(-10)).Select(r => r.EntryId));
    }

    [Fact]
    public void SentForStyle_drafted_without_draft_created_excludes_matching_replies()
    {
        _ix.UpsertBatch([Mail("r", folder: "Sent Items", irt: "<src>")]);
        _ix.UpsertDecision(new AiDecision("x", "<src>", null, T(-5), AiOutcome.Drafted));
        Assert.Empty(_ix.SentForStyle(T(-100), T(100)));
    }

    [Fact]
    public void SentForStyle_max_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SentForStyle(T(-1), T(0), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.SentForStyle(T(-1), T(0), 10_001));
        _ix.SentForStyle(T(-1), T(0), 10_000);
    }

    [Fact]
    public void DraftCandidates_picks_undecided_and_retryable_inbox_rows()
    {
        _ix.UpsertBatch([
            Mail("A", at: T(-1)), Mail("B", at: T(-2)), Mail("C", at: T(-3)), Mail("D", at: T(-4)),
            Mail("E", at: T(-5)), Mail("F", at: T(-9999)), Mail("G", at: T(-6), folder: "Inbox\\Sub"),
            Mail("H", at: T(-7), folder: "Sent Items"),
        ]);
        _ix.UpsertDecision(new AiDecision("B", null, null, Now, AiOutcome.Skipped, "replied"));
        _ix.UpsertDecision(new AiDecision("C", null, null, Now, AiOutcome.Failed, Attempts: 2));
        _ix.UpsertDecision(new AiDecision("D", null, null, Now, AiOutcome.Failed, Attempts: 3));
        _ix.UpsertDecision(new AiDecision("E", null, null, Now, AiOutcome.Drafted));
        Assert.Equal(["A", "C"], _ix.DraftCandidates(T(-100), 10).Select(c => c.EntryId));
        Assert.Equal(["A"], _ix.DraftCandidates(T(-100), 1).Select(c => c.EntryId));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.DraftCandidates(T(-100), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ix.DraftCandidates(T(-100), 501));
    }

    [Fact]
    public void Decision_round_trips_with_utc_seconds()
    {
        var local = new DateTimeOffset(2026, 11, 13, 4, 0, 0, 999, TimeSpan.FromHours(-8));
        var d = new AiDecision("E1", "<imid>", null, local, AiOutcome.Drafted, null, 2, 1, 0.83, "D1", local.AddMinutes(1));
        _ix.UpsertDecision(d);
        var got = _ix.GetDecision("E1")!;
        Assert.Equal(d with
        {
            Decided = DateTimeOffset.FromUnixTimeSeconds(local.ToUnixTimeSeconds()),
            DraftCreated = DateTimeOffset.FromUnixTimeSeconds(local.AddMinutes(1).ToUnixTimeSeconds()),
        }, got);
        Assert.Equal(TimeSpan.Zero, got.Decided.Offset);
        Assert.Null(_ix.GetDecision("nope"));
        Assert.Throws<ArgumentException>(() => _ix.GetDecision(""));
    }

    [Fact]
    public void Decision_topic_is_never_stored()
    {
        _ix.UpsertDecision(new AiDecision("E1", null, "Secret subject", Now, AiOutcome.NoReply));
        using var c = Raw();
        Assert.Null(c.ExecuteScalar<string?>("SELECT conversation_topic FROM ai_decisions"));
        Assert.Null(_ix.GetDecision("E1")!.ConversationTopic);
    }

    [Fact]
    public void Upsert_decision_replaces_the_row()
    {
        _ix.UpsertDecision(new AiDecision("E1", null, null, T(-10), AiOutcome.Failed, null, 1, 1));
        _ix.UpsertDecision(new AiDecision("E1", "<i>", null, T(-5), AiOutcome.Drafted, null, 2, 1, 0.9, "D", T(-5)));
        using var c = Raw();
        Assert.Equal(1, c.ExecuteScalar<long>("SELECT COUNT(*) FROM ai_decisions"));
        var got = _ix.GetDecision("E1")!;
        Assert.Equal(AiOutcome.Drafted, got.Outcome);
        Assert.Equal(2, got.ModelCalls);
        Assert.Equal("<i>", got.SourceImid);
    }

    public static TheoryData<AiDecision, string> BadDecisions() => new()
    {
        { new AiDecision("", null, null, Now, AiOutcome.Drafted), "AI decision: SourceEntryId is empty." },
        { new AiDecision("  ", null, null, Now, AiOutcome.Drafted), "AI decision: SourceEntryId is empty." },
        { new AiDecision("e", null, null, Now, (AiOutcome)9), "AI decision: unknown outcome." },
        { new AiDecision("e", null, null, Now, AiOutcome.Skipped), "AI decision: a skipped outcome needs a rule id." },
        { new AiDecision("e", null, null, Now, AiOutcome.Drafted, "replied"), "AI decision: only a skipped outcome has a rule id." },
        { new AiDecision("e", null, null, Now, AiOutcome.Skipped, "Has space"), "AI decision: rule ids are lower-case letters and '_'." },
        { new AiDecision("e", null, null, Now, AiOutcome.Skipped, new string('a', 33)), "AI decision: rule ids are lower-case letters and '_'." },
        { new AiDecision("e", null, null, Now, AiOutcome.Skipped, "bad\n"), "AI decision: rule ids are lower-case letters and '_'." },
        { new AiDecision("e", null, null, Now, AiOutcome.Drafted, ModelCalls: -1), "AI decision: counts can't be negative." },
        { new AiDecision("e", null, null, Now, AiOutcome.Drafted, Attempts: -1), "AI decision: counts can't be negative." },
        { new AiDecision("e", null, null, Now, AiOutcome.Drafted, Confidence: double.NaN), "AI decision: confidence must be between 0 and 1." },
        { new AiDecision("e", null, null, Now, AiOutcome.Drafted, Confidence: 1.1), "AI decision: confidence must be between 0 and 1." },
        { new AiDecision("e", null, null, Now, AiOutcome.Drafted, Confidence: -0.1), "AI decision: confidence must be between 0 and 1." },
    };

    [Theory]
    [MemberData(nameof(BadDecisions))]
    public void Invalid_decisions_are_rejected_and_nothing_is_stored(AiDecision d, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => _ix.UpsertDecision(d));
        Assert.Equal("decision", ex.ParamName);
        Assert.StartsWith(message, ex.Message);
        using var c = Raw();
        Assert.Equal(0, c.ExecuteScalar<long>("SELECT COUNT(*) FROM ai_decisions"));
        Assert.Throws<ArgumentNullException>(() => _ix.UpsertDecision(null!));
    }

    [Fact]
    public void Valid_rule_ids_are_accepted()
    {
        _ix.UpsertDecision(new AiDecision("e", null, null, Now, AiOutcome.Skipped, "replied"));
        _ix.UpsertDecision(new AiDecision("f", null, null, Now, AiOutcome.Skipped, "no_human_sender", Confidence: 1.0));
        Assert.Equal("replied", _ix.GetDecision("e")!.Rule);
    }

    [Fact]
    public void Unknown_stored_outcome_reads_as_skipped()
    {
        _ix.UpsertDecision(new AiDecision("e", null, null, Now, AiOutcome.Drafted));
        using (var c = Raw()) c.Execute("UPDATE ai_decisions SET outcome = 'weird'");
        Assert.Equal(AiOutcome.Skipped, _ix.GetDecision("e")!.Outcome);
    }

    [Fact]
    public void AiOutcomes_map_both_ways()
    {
        foreach (var o in Enum.GetValues<AiOutcome>())
            Assert.Equal(o, AiOutcomes.FromDb(AiOutcomes.ToDb(o)));
        Assert.Equal("no_reply", AiOutcomes.ToDb(AiOutcome.NoReply));
        Assert.Equal("low_confidence", AiOutcomes.ToDb(AiOutcome.LowConfidence));
        Assert.Equal(AiOutcome.Skipped, AiOutcomes.FromDb(null));
        Assert.Equal(AiOutcome.Skipped, AiOutcomes.FromDb("Drafted"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AiOutcomes.ToDb((AiOutcome)9));
    }

    [Fact]
    public void ModelCallsSince_sums_recent_rows()
    {
        Assert.Equal(0, _ix.ModelCallsSince(T(-1000)));
        _ix.UpsertDecision(new AiDecision("a", null, null, Now.AddHours(-2), AiOutcome.Drafted, ModelCalls: 2));
        _ix.UpsertDecision(new AiDecision("b", null, null, Now.AddDays(-1), AiOutcome.Drafted, ModelCalls: 3));
        _ix.UpsertDecision(new AiDecision("c", null, null, Now, AiOutcome.Drafted, ModelCalls: 1));
        Assert.Equal(3, _ix.ModelCallsSince(Now.AddHours(-3)));
    }

    [Fact]
    public void PurgeDecisions_removes_only_rows_older_than_30_days()
    {
        _ix.UpsertDecision(new AiDecision("a", null, null, Now.AddDays(-31), AiOutcome.Drafted));
        _ix.UpsertDecision(new AiDecision("b", null, null, Now.AddDays(-30).AddSeconds(-1), AiOutcome.Drafted));
        _ix.UpsertDecision(new AiDecision("c", null, null, Now.AddDays(-30), AiOutcome.Drafted));
        _ix.UpsertDecision(new AiDecision("d", null, null, Now.AddDays(-1), AiOutcome.Drafted));
        Assert.Equal(2, _ix.PurgeDecisions(Now));
        Assert.Null(_ix.GetDecision("a"));
        Assert.Null(_ix.GetDecision("b"));
        Assert.NotNull(_ix.GetDecision("c"));
        Assert.NotNull(_ix.GetDecision("d"));
    }

    [Fact]
    public void PurgeOlderThan_also_purges_old_decisions()
    {
        var utc = DateTimeOffset.UtcNow;
        _ix.UpsertDecision(new AiDecision("old", null, null, utc.AddDays(-40), AiOutcome.Drafted));
        _ix.UpsertDecision(new AiDecision("new", null, null, utc.AddDays(-1), AiOutcome.Drafted));
        Assert.Equal(0, _ix.PurgeOlderThan(DateTimeOffset.FromUnixTimeSeconds(0)));
        Assert.Null(_ix.GetDecision("old"));
        Assert.NotNull(_ix.GetDecision("new"));
    }

    [Fact]
    public void Ai_decisions_has_no_mail_text_columns()
    {
        using var c = Raw();
        Assert.Equal(
            ["source_entry_id", "source_imid", "conversation_topic", "decided", "outcome", "rule", "model_calls",
             "attempts", "confidence", "draft_entry_id", "draft_created"],
            c.Query<string>("SELECT name FROM pragma_table_info('ai_decisions') ORDER BY cid").ToArray());
    }
}
