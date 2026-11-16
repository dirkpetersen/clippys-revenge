using Dapper;
using Microsoft.Data.Sqlite;

namespace ClippysRevenge.Core.Tests;

public class EditLearningTests : IDisposable
{
    private const string Shared = "helpdesk@example.org";
    private const string DraftA = "Hi Ann, thanks for the update. I will check and get back to you tomorrow.";
    private const string SentFriday = "Hi Ann, thanks for the update. I will check and get back to you on Friday.";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-editlearn-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly MailboxIndexes _indexes;
    private readonly Settings _settings = new();
    private readonly ActivityHub _hub;
    private readonly CapturingLog _log = new();
    private readonly EditLearning _sut;
    private IReadOnlyList<string> _sigs = ["Peter"];
    private bool _sigsThrow;

    private DateTimeOffset T => _time.GetUtcNow();
    private EmailIndex Own => _indexes.For("");

    public EditLearningTests()
    {
        _indexes = new MailboxIndexes(_dir, [Shared]);
        _hub = new ActivityHub(_time);
        _settings.AiDrafting.RecordConsent(T.AddDays(-1));
        _sut = new EditLearning(_indexes, () => _settings.AiDrafting,
            () => _sigsThrow ? throw new InvalidOperationException("sig") : _sigs, _hub, _time, _log);
    }

    public void Dispose()
    {
        _hub.Dispose();
        _indexes.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Sent(string id, DateTimeOffset at, string body, string? marker = "ai", string? irt = "<A@x>", string folder = "Sent Items")
    {
        Own.UpsertBatch([new IndexedEmail
        {
            EntryId = id, Folder = folder, ClippyMarker = marker, InReplyTo = irt, EventTime = at, Body = body, Subject = "s",
        }]);
    }

    private static string SentBody(string text) =>
        "Clippy: I drafted this because SECRETWORD.\n\n" + text +
        "\n\nPeter\n\nFrom: Ann\nSent: Monday\nTo: Peter\nSubject: x\n\nold SECRETWORD";

    private void Draft(string id, DateTimeOffset created, string text, string imid = "<A@x>", string mailbox = "") =>
        Own.RecordAiDraftText(id, mailbox, imid, created, text);

    private SqliteConnection Raw()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Own.DbPath, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static SyncCycleResult Cycle(string mailbox = "", bool notMounted = false) =>
        new([new MailboxSyncResult(mailbox, notMounted, [], 0, false, TimeSpan.Zero)], false);

    private IEnumerable<string> Everything() =>
        _hub.GetEvents().Select(e => e.Summary).Concat(_log.Lines);

    private IReadOnlyList<ActivityEvent> AiEvents() => _hub.GetEvents().Where(e => e.Kind == ActivityKind.Ai).ToList();

    [Fact]
    public async Task Match_stores_the_pair_and_raises_one_event()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(new EditMatchResult(true, 1, 0, 0), r);
        var e = Assert.Single(Own.AiEdits(10));
        Assert.Equal("S1", e.SentEntryId);
        Assert.Equal(34.0 / 37, e.Similarity);
        Assert.Equal(SentFriday, e.SentText);
        Assert.Equal(DraftA, e.DraftText);
        Assert.Equal(DraftA.Length, e.DraftChars);
        Assert.Equal(SentFriday.Length, e.SentChars);
        Assert.Equal(AiDraftTextState.Matched, Own.GetAiDraftText("D1")!.State);
        Assert.Equal("AI drafts: noted how you edited 1 sent draft.", Assert.Single(AiEvents()).Summary);
    }

    [Fact]
    public async Task Unchanged_pair_keeps_numbers_only()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(DraftA));

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(new EditMatchResult(true, 1, 1, 0), r);
        var e = Assert.Single(Own.AiEdits(10));
        Assert.Equal(1.0, e.Similarity);
        Assert.Null(e.DraftText);
        Assert.Null(e.SentText);
        Assert.Equal(DraftA.Length, e.DraftChars);
        Assert.Single(AiEvents());
    }

    [Fact]
    public async Task Newest_draft_text_before_the_sent_time_wins()
    {
        Draft("DA", T.AddHours(-3), "A text one");
        Draft("DB", T.AddHours(-2), "B text two");
        Draft("DC", T.AddMinutes(-30), "C text three");
        Sent("S1", T.AddHours(-1), SentBody("Something else entirely"));

        await _sut.MatchAsync(Ct);

        Assert.Equal("B text two", Assert.Single(Own.AiEdits(10)).DraftText);
        Assert.Equal(AiDraftTextState.Superseded, Own.GetAiDraftText("DA")!.State);
        Assert.Equal(AiDraftTextState.Matched, Own.GetAiDraftText("DB")!.State);
        Assert.Equal(AiDraftTextState.Pending, Own.GetAiDraftText("DC")!.State);
    }

    [Fact]
    public async Task Rows_that_do_not_qualify_are_never_matched()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("N1", T.AddHours(-1), SentBody(SentFriday), marker: null);
        Sent("N2", T.AddHours(-1), SentBody(SentFriday), marker: "tool");
        Sent("N3", T.AddHours(-1), SentBody(SentFriday), irt: " ");
        Sent("N4", T.AddDays(-31), SentBody(SentFriday));
        Sent("N5", T.AddHours(-3), SentBody(SentFriday));   // only a newer draft text exists for it

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(new EditMatchResult(true, 0, 0, 0), r);
        Assert.Empty(Own.AiEdits(10));
        Assert.Empty(AiEvents());
    }

    [Fact]
    public async Task A_sent_row_is_matched_once()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));

        await _sut.MatchAsync(Ct);
        var second = await _sut.MatchAsync(Ct);

        Assert.Equal(new EditMatchResult(true, 0, 0, 0), second);
        Assert.Single(Own.AiEdits(10));
        Assert.Single(AiEvents());
    }

    [Fact]
    public async Task Shared_mailbox_draft_matches_a_sent_row_of_the_own_mailbox()
    {
        Draft("DS", T.AddHours(-2), DraftA, mailbox: Shared);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(1, r.Stored);
        Assert.Equal(DraftA, Assert.Single(Own.AiEdits(10)).DraftText);
        Assert.Empty(_indexes.For(Shared).AiEdits(10));
    }

    [Fact]
    public async Task Setting_off_or_no_consent_does_not_run()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));

        _settings.AiDrafting.LearnFromEdits = false;
        Assert.Equal(EditMatchResult.NotRun, await _sut.MatchAsync(Ct));
        _settings.AiDrafting.LearnFromEdits = true;
        _settings.AiDrafting.ConsentedAtUtc = null;
        Assert.Equal(EditMatchResult.NotRun, await _sut.MatchAsync(Ct));

        Assert.Empty(Own.AiEdits(10));
    }

    [Fact]
    public async Task Hook_starts_a_run_at_most_every_five_minutes_and_only_for_the_own_mailbox()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));

        var first = _sut.OnSyncCycleCompleted(Cycle());
        Assert.NotNull(first);
        Assert.Equal(1, (await first).Stored);

        Assert.Null(_sut.OnSyncCycleCompleted(Cycle()));
        _time.Advance(new TimeSpan(0, 4, 59));
        Assert.Null(_sut.OnSyncCycleCompleted(Cycle()));
        _time.Advance(TimeSpan.FromSeconds(1));
        var again = _sut.OnSyncCycleCompleted(Cycle());
        Assert.NotNull(again);
        await again;
    }

    [Fact]
    public async Task Hook_ignores_cycles_without_a_usable_own_result_and_keeps_the_timer()
    {
        Assert.Null(_sut.OnSyncCycleCompleted(null));
        Assert.Null(_sut.OnSyncCycleCompleted(Cycle(Shared)));
        Assert.Null(_sut.OnSyncCycleCompleted(Cycle(notMounted: true)));
        _settings.AiDrafting.LearnFromEdits = false;
        Assert.Null(_sut.OnSyncCycleCompleted(Cycle()));
        _settings.AiDrafting.LearnFromEdits = true;

        var t = _sut.OnSyncCycleCompleted(Cycle());   // starts at once: none of the above set the timer
        Assert.NotNull(t);
        await t;
    }

    [Fact]
    public async Task A_failing_listing_is_one_warning_and_never_throws()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));
        using (var c = Raw()) c.Execute("DROP TABLE ai_edits");

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(new EditMatchResult(true, 0, 0, 0), r);
        Assert.Single(_log.Lines, l => l == "AI drafts: learning from edits failed (SqliteException).");
    }

    [Fact]
    public async Task A_throwing_signature_source_counts_as_none()
    {
        _sigsThrow = true;
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("S1", T.AddHours(-1), SentBody(SentFriday));

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(1, r.Stored);
        Assert.Contains("Peter", Assert.Single(Own.AiEdits(10)).SentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_and_events_hold_no_texts_ids_or_imids()
    {
        Draft("D1", T.AddHours(-2), DraftA);
        Sent("SENTID-77", T.AddHours(-1), SentBody(SentFriday));
        await _sut.MatchAsync(Ct);
        using (var c = Raw()) c.Execute("DROP TABLE ai_edits");
        await _sut.MatchAsync(Ct);

        foreach (var bad in new[] { "SECRETWORD", "tomorrow", "Friday", "<A@x>", "SENTID-77" })
            Assert.DoesNotContain(Everything(), t => t.Contains(bad, StringComparison.Ordinal));
        Assert.NotEmpty(_log.Lines);
    }

    [Fact]
    public async Task Twenty_one_matches_in_one_run_keep_twenty_texts()
    {
        for (var i = 0; i < 21; i++)
        {
            var imid = "<M" + i + "@x>";
            Draft("D" + i, T.AddHours(-3), "draft " + i, imid);
            Sent("S" + i, T.AddHours(-1).AddMinutes(i), SentBody("different words " + i), irt: imid);
        }

        var r = await _sut.MatchAsync(Ct);

        Assert.Equal(21, r.Stored);
        var rows = Own.AiEdits(100);
        Assert.Equal(21, rows.Count);
        Assert.Equal(20, rows.Count(x => x.DraftText is not null));
        Assert.Null(rows[^1].DraftText);
        Assert.Equal("AI drafts: noted how you edited 21 sent drafts.", Assert.Single(AiEvents()).Summary);
    }
}
