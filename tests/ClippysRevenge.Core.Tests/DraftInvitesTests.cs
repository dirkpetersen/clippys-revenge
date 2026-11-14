using Xunit;

namespace ClippysRevenge.Core.Tests;

internal sealed class FakeDraftInviteCalendar : IDraftInviteCalendar
{
    private readonly Lock _lock = new();
    private readonly List<(string[] Ids, WorkPriority Priority)> _calls = [];
    private readonly List<(string Id, string? NewCategories)> _writes = [];

    public Dictionary<string, DraftInviteSnapshot> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailWrites { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? Throw { get; set; }
    public TaskCompletionSource? Gate { get; set; }

    public List<(string[] Ids, WorkPriority Priority)> Calls { get { lock (_lock) return [.. _calls]; } }
    public List<(string Id, string? NewCategories)> Writes { get { lock (_lock) return [.. _writes]; } }

    public async Task<IReadOnlyList<DraftInviteOutcome>> CheckAsync(IReadOnlyList<DraftInviteEntry> entries,
        Func<DraftInviteSnapshot, DraftInviteDecision> decide, WorkPriority priority, CancellationToken ct)
    {
        lock (_lock) _calls.Add(([.. entries.Select(e => e.EntryId)], priority));
        var gate = Gate;
        if (gate is not null) await gate.Task;
        if (Throw is not null) throw Throw;
        var outcomes = new List<DraftInviteOutcome>();
        foreach (var e in entries)
        {
            var snap = Snapshots.TryGetValue(e.EntryId, out var s) ? s : new DraftInviteSnapshot(DraftInviteLookup.NotFound);
            var d = decide(snap);
            var applied = false;
            if (d.Action == DraftInviteAction.RemoveCategory && !FailWrites.Contains(e.EntryId))
            {
                lock (_lock) _writes.Add((e.EntryId, d.NewCategories));
                applied = true;
            }
            outcomes.Add(new DraftInviteOutcome(e, d, snap.Details, applied));
        }
        return outcomes;
    }
}

public sealed class DraftInvitesTests : IDisposable
{
    private const string Cat = "Clippy draft invite";
    private static readonly DateTimeOffset NowUtc = new(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowLocal = new(2026, 11, 14, 10, 0, 0);
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test-8", TimeSpan.FromHours(-8), "Test-8", "Test-8");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-di-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Rig
    {
        private int _changed;

        public Rig(string dir, string? storePath = null, bool available = true)
        {
            Time.Advance(NowUtc - ManualTimeProvider.T0);
            Available = available;
            Store = new DraftInvitesStore(storePath ?? Path.Combine(dir, DraftInvitesStore.FileName), Log);
            Dc = new DraftInvites(Cal, Store, () => Available, Tz, Time, Log, ",");
            Dc.Changed += () => Interlocked.Increment(ref _changed);
        }

        public ManualTimeProvider Time { get; } = new();
        public CapturingLog Log { get; } = new();
        public FakeDraftInviteCalendar Cal { get; } = new();
        public DraftInvitesStore Store { get; }
        public DraftInvites Dc { get; }
        public bool Available { get; set; }
        public int Changed => Volatile.Read(ref _changed);
        public void ResetChanged() => Interlocked.Exchange(ref _changed, 0);

        public DraftInviteEntry Add(string id, int minutes = 0, DraftInviteDetails? details = null)
        {
            var e = new DraftInviteEntry(id, "S", "", NowUtc.AddMinutes(-1000 + minutes));
            Dc.Track(e, details);
            ResetChanged();
            return e;
        }
    }

    private static DraftInviteDetails Det(DateTime start) => new("Subj", start, start.AddHours(1));

    private static DraftInviteSnapshot Sent(string cats = "Red, " + Cat + ", Blue", bool inspector = false) =>
        new(DraftInviteLookup.Found, MeetingStatus: 1, Invited: true, Categories: cats, OpenInInspector: inspector);

    private static DraftInviteSnapshot Unsent(DraftInviteDetails? d = null) =>
        new(DraftInviteLookup.Found, MeetingStatus: 1, Invited: false, Categories: Cat, Details: d);

    private static async Task WaitUntil(Func<bool> cond)
    {
        for (var i = 0; i < 500 && !cond(); i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(cond(), "condition not reached");
    }

    // ---- Decide ----

    private static DraftInviteDecision D(DraftInviteSnapshot s) => DraftInvites.Decide(s, NowLocal, ",");

    private static DraftInviteSnapshot F(int status = 1, bool? invited = null, string cats = "Red, " + Cat,
        bool deleted = false, bool inspector = false, DraftInviteDetails? details = null) =>
        new(DraftInviteLookup.Found, deleted, status, invited, cats, details, inspector);

    public static TheoryData<int, DraftInviteAction, DraftInviteReason, string?> DecideCases() => new()
    {
        { 1, DraftInviteAction.Drop, DraftInviteReason.NotFound, null },
        { 2, DraftInviteAction.Keep, DraftInviteReason.Unreadable, null },
        { 3, DraftInviteAction.Drop, DraftInviteReason.Deleted, null },
        { 4, DraftInviteAction.RemoveCategory, DraftInviteReason.Sent, "Red" },
        { 5, DraftInviteAction.Keep, DraftInviteReason.Unsent, null },
        { 6, DraftInviteAction.Keep, DraftInviteReason.Unsent, null },
        { 7, DraftInviteAction.RemoveCategory, DraftInviteReason.NotAMeeting, "Red" },
        { 8, DraftInviteAction.RemoveCategory, DraftInviteReason.Sent, "Red" },
        { 9, DraftInviteAction.RemoveCategory, DraftInviteReason.Cancelled, "Red" },
        { 10, DraftInviteAction.RemoveCategory, DraftInviteReason.Cancelled, "Red" },
        { 11, DraftInviteAction.Drop, DraftInviteReason.CategoryGone, null },
        { 12, DraftInviteAction.Drop, DraftInviteReason.CategoryGone, null },
        { 13, DraftInviteAction.Keep, DraftInviteReason.OpenInInspector, null },
        { 14, DraftInviteAction.Drop, DraftInviteReason.CategoryGone, null },
        { 15, DraftInviteAction.Drop, DraftInviteReason.Expired, null },
        { 16, DraftInviteAction.Keep, DraftInviteReason.Unsent, null },
        { 17, DraftInviteAction.Keep, DraftInviteReason.Unsent, null },
        { 18, DraftInviteAction.RemoveCategory, DraftInviteReason.Sent, "Red" },
    };

    private static DraftInviteSnapshot Case(int n) => n switch
    {
        1 => new(DraftInviteLookup.NotFound),
        2 => new(DraftInviteLookup.Unreadable),
        3 => F(invited: true, deleted: true),
        4 => F(invited: true),
        5 => F(invited: false),
        6 => F(invited: null),
        7 => F(status: 0, invited: false),
        8 => F(status: 5, invited: true),
        9 => F(status: 5, invited: null),
        10 => F(status: 7, invited: false),
        11 => F(invited: true, cats: "Red"),
        12 => F(invited: true, cats: ""),
        13 => F(invited: true, inspector: true),
        14 => F(invited: true, cats: "Red", inspector: true),
        15 => F(invited: false, details: new("S", NowLocal.AddDays(-31), NowLocal.AddDays(-30).AddMinutes(-1))),
        16 => F(invited: false, details: new("S", NowLocal.AddDays(-31), NowLocal.AddDays(-30))),
        17 => F(invited: false, details: null),
        18 => F(invited: true, details: new("S", NowLocal.AddDays(-91), NowLocal.AddDays(-90))),
        _ => throw new ArgumentOutOfRangeException(nameof(n)),
    };

    [Theory]
    [MemberData(nameof(DecideCases))]
    public void Decide_table(int n, DraftInviteAction action, DraftInviteReason reason, string? next)
    {
        var d = D(Case(n));
        Assert.Equal(action, d.Action);
        Assert.Equal(reason, d.Reason);
        Assert.Equal(next, d.NewCategories);
    }

    // ---- Check ----

    [Fact]
    public async Task Sent_removes_only_our_category_and_drops()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent();
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal([("E1", "Red, Blue")], t.Cal.Writes);
        Assert.Equal(0, t.Dc.Count);
        Assert.Empty(new DraftInvitesStore(t.Store.Path).Load());
        Assert.Equal(1, t.Changed);
        Assert.Equal(NowUtc, t.Dc.LastCheckUtc);
    }

    [Fact]
    public async Task Unsent_is_kept_without_write()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        var d = Det(NowLocal.AddDays(2));
        t.Cal.Snapshots["E1"] = Unsent(d);
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Writes);
        Assert.Equal(1, t.Dc.Count);
        Assert.Equal(d, t.Dc.Pending[0].Details);
        Assert.Equal(1, t.Changed);
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(1, t.Changed);
    }

    [Fact]
    public async Task Category_already_gone_drops_without_write()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent("Red");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Writes);
        Assert.Equal(0, t.Dc.Count);
    }

    [Fact]
    public async Task Deleted_drops_without_write()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent() with { InDeletedItems = true };
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Writes);
        Assert.Equal(0, t.Dc.Count);
    }

    [Fact]
    public async Task Not_found_drops_without_write()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Writes);
        Assert.Equal(0, t.Dc.Count);
    }

    [Fact]
    public async Task Open_in_inspector_retries()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent(inspector: true);
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Writes);
        Assert.Equal(1, t.Dc.Count);
        t.Cal.Snapshots["E1"] = Sent();
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Single(t.Cal.Writes);
        Assert.Equal(0, t.Dc.Count);
    }

    [Fact]
    public async Task Expired_unsent_drops_without_write()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Unsent(new DraftInviteDetails("S", NowLocal.AddDays(-32), NowLocal.AddDays(-31)));
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Writes);
        Assert.Equal(0, t.Dc.Count);
    }

    [Fact]
    public async Task Not_a_meeting_and_cancelled_remove_category()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Add("E2", 1);
        t.Cal.Snapshots["E1"] = F(status: 0, invited: false);
        t.Cal.Snapshots["E2"] = F(status: 5, invited: false);
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(2, t.Cal.Writes.Count);
        Assert.Equal(0, t.Dc.Count);
    }

    [Fact]
    public async Task Write_failure_keeps_entry()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent();
        t.Cal.FailWrites.Add("E1");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(1, t.Dc.Count);
    }

    [Fact]
    public async Task Unreadable_is_kept()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = new DraftInviteSnapshot(DraftInviteLookup.Unreadable);
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(1, t.Dc.Count);
        Assert.Empty(t.Cal.Writes);
    }

    [Fact]
    public async Task At_most_50_per_check_oldest_first_then_rest()
    {
        var t = new Rig(_dir);
        for (var i = 0; i < 60; i++)
        {
            var id = $"E{i:00}";
            t.Add(id, i);
            t.Cal.Snapshots[id] = Unsent();
        }
        await t.Dc.CheckAsync(WorkPriority.Background, TestContext.Current.CancellationToken);
        await t.Dc.CheckAsync(WorkPriority.Background, TestContext.Current.CancellationToken);
        var calls = t.Cal.Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"E{i:00}"), calls[0].Ids);
        Assert.Equal(50, calls[1].Ids.Length);
        Assert.Equal(Enumerable.Range(50, 10).Select(i => $"E{i}"), calls[1].Ids.Take(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outlook_unavailable_leaves_store_unchanged(bool notResponding)
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Add("E2", 1);
        var before = File.ReadAllBytes(t.Store.Path);
        t.Cal.Throw = notResponding ? new OutlookNotRespondingException() : new OutlookUnavailableException();
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(2, t.Dc.Count);
        Assert.Equal(before, File.ReadAllBytes(t.Store.Path));
        Assert.Equal(0, t.Changed);
        Assert.Null(t.Dc.LastCheckUtc);
    }

    [Fact]
    public async Task Other_exception_logged_store_unchanged()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Throw = new InvalidOperationException("x");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Contains("Draft invite check failed (InvalidOperationException).", t.Log.Lines);
        Assert.Equal(1, t.Dc.Count);
        Assert.DoesNotContain(t.Log.Lines, l => l.Contains("E1"));
    }

    [Fact]
    public async Task No_com_when_unavailable_or_empty()
    {
        var t = new Rig(_dir, available: false);
        t.Add("E1");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(t.Cal.Calls);

        var u = new Rig(Path.Combine(_dir, "u"));
        await u.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Empty(u.Cal.Calls);
    }

    [Fact]
    public async Task Single_flight()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Unsent();
        t.Cal.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        var b = t.Dc.CheckAsync(WorkPriority.Background, TestContext.Current.CancellationToken);
        await WaitUntil(() => t.Cal.Calls.Count == 1);
        t.Cal.Gate.SetResult();
        var ra = await a;
        var rb = await b;
        Assert.Single(t.Cal.Calls);
        Assert.Equal(ra, rb);
    }

    [Fact]
    public async Task Caller_cancel_only_stops_waiting()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent();
        t.Cal.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var run = t.Dc.CheckAsync(WorkPriority.Interactive, cts.Token);
        await WaitUntil(() => t.Cal.Calls.Count == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        t.Cal.Gate.SetResult();
        await WaitUntil(() => t.Dc.Count == 0);
        Assert.Single(t.Cal.Writes);
    }

    [Fact]
    public void Track_adds_persists_and_raises()
    {
        var t = new Rig(_dir);
        var d = Det(NowLocal.AddDays(1));
        t.Dc.Track(new DraftInviteEntry("E1", "S", "", NowUtc), d);
        Assert.Equal(1, t.Dc.Count);
        Assert.Equal("E1", Assert.Single(new DraftInvitesStore(t.Store.Path).Load()).EntryId);
        Assert.Equal(d, t.Dc.Pending[0].Details);
        Assert.Equal(1, t.Changed);
        Assert.Empty(t.Cal.Calls);
    }

    [Fact]
    public void Track_dedupes_case_insensitive()
    {
        var t = new Rig(_dir);
        t.Dc.Track(new DraftInviteEntry("ab12", "S", "", NowUtc), null);
        var d = Det(NowLocal.AddDays(1));
        t.Dc.Track(new DraftInviteEntry("AB12", "S", "", NowUtc.AddHours(1)), d);
        Assert.Equal(1, t.Dc.Count);
        var p = t.Dc.Pending[0];
        Assert.Equal(NowUtc, p.Entry.CreatedUtc);
        Assert.Equal(d, p.Details);
    }

    [Fact]
    public void Track_cap_200()
    {
        var t = new Rig(_dir);
        for (var i = 0; i < 201; i++) t.Dc.Track(new DraftInviteEntry("E" + i, "S", "", NowUtc.AddMinutes(i)), null);
        Assert.Equal(200, t.Dc.Count);
        Assert.DoesNotContain(t.Dc.Pending, p => p.Entry.EntryId == "E0");
    }

    [Fact]
    public void Track_blank_id_ignored()
    {
        var t = new Rig(_dir);
        t.Dc.Track(new DraftInviteEntry("", "S", "", NowUtc), null);
        t.Dc.Track(new DraftInviteEntry("  ", "S", "", NowUtc), null);
        Assert.Equal(0, t.Dc.Count);
        Assert.Equal(2, t.Log.Lines.Count(l => l.Contains("has no entry id")));
    }

    [Fact]
    public async Task Track_during_check_is_kept()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Sent();
        t.Cal.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        await WaitUntil(() => t.Cal.Calls.Count == 1);
        t.Dc.Track(new DraftInviteEntry("E2", "S", "", NowUtc), null);
        t.Cal.Gate.SetResult();
        await run;
        Assert.Equal(["E2"], t.Dc.Pending.Select(p => p.Entry.EntryId));
        Assert.Equal(["E2"], new DraftInvitesStore(t.Store.Path).Load().Select(e => e.EntryId));
    }

    [Fact]
    public void Forget()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        Assert.True(t.Dc.Forget("E1"));
        Assert.Equal(1, t.Changed);
        Assert.Empty(new DraftInvitesStore(t.Store.Path).Load());
        Assert.False(t.Dc.Forget("E9"));
        Assert.Equal(1, t.Changed);
    }

    [Fact]
    public void Reload_has_no_details()
    {
        var t = new Rig(_dir);
        t.Dc.Track(new DraftInviteEntry("E1", "S", "", NowUtc), Det(NowLocal));
        var again = new DraftInvites(t.Cal, t.Store, () => true, Tz, t.Time, t.Log, ",");
        Assert.Equal(1, again.Count);
        Assert.Null(again.Pending[0].Details);
    }

    [Fact]
    public void Pending_sorted_by_start()
    {
        var t = new Rig(_dir);
        t.Dc.Track(new DraftInviteEntry("E1", "S", "", NowUtc), Det(NowLocal.AddDays(3)));
        t.Dc.Track(new DraftInviteEntry("E2", "S", "", NowUtc.AddMinutes(1)), Det(NowLocal.AddDays(1)));
        t.Dc.Track(new DraftInviteEntry("E3", "S", "", NowUtc.AddMinutes(2)), Det(NowLocal.AddDays(2)));
        t.Dc.Track(new DraftInviteEntry("E4", "S", "", NowUtc.AddMinutes(3)), null);
        Assert.Equal(["E2", "E3", "E1", "E4"], t.Dc.Pending.Select(p => p.Entry.EntryId));
    }

    [Fact]
    public void Save_failure_keeps_memory()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "x");
        File.WriteAllText(blocker, "file");
        var t = new Rig(_dir, storePath: Path.Combine(blocker, DraftInvitesStore.FileName));
        t.Dc.Track(new DraftInviteEntry("E1", "S", "", NowUtc), null);
        Assert.Equal(1, t.Dc.Count);
        Assert.Contains(t.Log.Lines, l => l.Contains("draft-invites.json could not be saved"));
    }

    [Fact]
    public async Task Background_throttle()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Unsent();
        var first = t.Dc.OnSyncCycleCompleted();
        Assert.NotNull(first);
        await first;
        Assert.Equal(WorkPriority.Background, Assert.Single(t.Cal.Calls).Priority);

        t.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Null(t.Dc.OnSyncCycleCompleted());
        Assert.Single(t.Cal.Calls);

        t.Time.Advance(TimeSpan.FromMinutes(1));
        var second = t.Dc.OnSyncCycleCompleted();
        Assert.NotNull(second);
        await second;
        Assert.Equal(2, t.Cal.Calls.Count);
    }

    [Fact]
    public void Background_needs_availability_and_entries()
    {
        var off = new Rig(_dir, available: false);
        off.Add("E1");
        Assert.Null(off.Dc.OnSyncCycleCompleted());
        var empty = new Rig(Path.Combine(_dir, "e"));
        Assert.Null(empty.Dc.OnSyncCycleCompleted());
    }

    [Fact]
    public async Task Background_not_while_in_flight()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Cal.Snapshots["E1"] = Unsent();
        t.Cal.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = t.Dc.OnSyncCycleCompleted();
        Assert.NotNull(first);
        Assert.Null(t.Dc.OnSyncCycleCompleted());
        t.Cal.Gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Interactive_priority_passed()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(WorkPriority.Interactive, Assert.Single(t.Cal.Calls).Priority);
    }

    [Fact]
    public void Changed_handler_exception_isolated()
    {
        var t = new Rig(_dir);
        var second = 0;
        t.Dc.Changed += () => throw new InvalidOperationException("boom");
        t.Dc.Changed += () => second++;
        t.Dc.Track(new DraftInviteEntry("E1", "S", "", NowUtc), null);
        Assert.Equal(1, second);
        Assert.Contains(t.Log.Lines, l => l.Contains("Changed handler failed (InvalidOperationException)"));
    }

    [Fact]
    public async Task Logs_have_no_ids_or_categories()
    {
        var t = new Rig(_dir);
        t.Add("E1");
        t.Add("E2", 1);
        t.Add("E3", 2);
        t.Cal.Snapshots["E1"] = Sent();
        t.Cal.Snapshots["E2"] = Sent(inspector: true);
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        t.Cal.Throw = new InvalidOperationException("E3 Red");
        await t.Dc.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.NotEmpty(t.Log.Lines);
        Assert.DoesNotContain(t.Log.Lines, l => l.Contains("E1") || l.Contains("E2") || l.Contains("E3") || l.Contains("Red") || l.Contains("Blue"));
    }
}
