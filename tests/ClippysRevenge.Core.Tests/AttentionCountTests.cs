using Xunit;

namespace ClippysRevenge.Core.Tests;

public sealed class AttentionCountTests : IDisposable
{
    private sealed class FakeSource : IAttentionSource
    {
        private int _count;
        public int Count
        {
            get
            {
                if (ThrowOnRead) throw new InvalidOperationException("boom");
                return Volatile.Read(ref _count);
            }
            set => Volatile.Write(ref _count, value);
        }
        public bool ThrowOnRead { get; set; }
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
    }

    private sealed class ThrowingCalendar : IDraftInviteCalendar
    {
        public Task<IReadOnlyList<DraftInviteOutcome>> CheckAsync(IReadOnlyList<DraftInviteEntry> entries,
            Func<DraftInviteSnapshot, DraftInviteDecision> decide, WorkPriority priority, CancellationToken ct) =>
            throw new OutlookUnavailableException();
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-ac-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Sums_sources()
    {
        Assert.Equal(5, new AttentionCount([new FakeSource { Count = 2 }, new FakeSource { Count = 3 }]).Total);
        Assert.Equal(0, new AttentionCount([]).Total);
    }

    [Fact]
    public void Args()
    {
        Assert.Throws<ArgumentNullException>(() => new AttentionCount(null!));
        Assert.Throws<ArgumentException>(() => new AttentionCount([new FakeSource(), null!]));
    }

    [Fact]
    public void Changed_only_when_total_changes()
    {
        var a = new FakeSource { Count = 2 };
        var b = new FakeSource { Count = 3 };
        var ac = new AttentionCount([a, b]);
        var changed = 0;
        ac.Changed += () => changed++;
        a.Count = 4;
        a.Raise();
        Assert.Equal(7, ac.Total);
        Assert.Equal(1, changed);
        a.Raise();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Negative_and_overflow()
    {
        Assert.Equal(0, new AttentionCount([new FakeSource { Count = -5 }]).Total);
        Assert.Equal(int.MaxValue, new AttentionCount([new FakeSource { Count = int.MaxValue }, new FakeSource { Count = 5 }]).Total);
    }

    [Fact]
    public void Source_throw_keeps_last_total()
    {
        var log = new CapturingLog();
        var s = new FakeSource { Count = 3 };
        var ac = new AttentionCount([s], log);
        var changed = 0;
        ac.Changed += () => changed++;
        s.ThrowOnRead = true;
        s.Raise();
        Assert.Equal(3, ac.Total);
        Assert.Equal(0, changed);
        Assert.Single(log.Lines, l => l.Contains("Attention count could not be read (InvalidOperationException)."));
    }

    [Fact]
    public void Handler_exception_isolated()
    {
        var log = new CapturingLog();
        var s = new FakeSource();
        var ac = new AttentionCount([s], log);
        var second = 0;
        ac.Changed += () => throw new InvalidOperationException("x");
        ac.Changed += () => second++;
        s.Count = 1;
        s.Raise();
        Assert.Equal(1, second);
        Assert.Single(log.Lines, l => l.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Concurrent_raises()
    {
        var sources = Enumerable.Range(0, 4).Select(_ => new FakeSource()).ToArray();
        var ac = new AttentionCount(sources);
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            var rnd = new Random(t);
            for (var i = 0; i < 1000; i++)
            {
                var s = sources[rnd.Next(sources.Length)];
                s.Count = rnd.Next(0, 20);
                s.Raise();
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        foreach (var s in sources) s.Raise();
        Assert.Equal(sources.Sum(s => s.Count), ac.Total);
    }

    [Fact]
    public async Task Keeps_last_count_while_outlook_unavailable()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, DraftInvitesStore.FileName);
        var store = new DraftInvitesStore(path);
        var now = new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero);
        store.Save([new("E1", "S", "", now), new("E2", "S", "", now)]);
        var available = true;
        var invites = new DraftInvites(new ThrowingCalendar(), new DraftInvitesStore(path), () => available,
            TimeZoneInfo.Utc, new ManualTimeProvider(), new CapturingLog(), ",");
        var ac = new AttentionCount([invites]);
        var changed = 0;
        ac.Changed += () => changed++;
        Assert.Equal(2, ac.Total);

        await invites.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(2, ac.Total);
        Assert.Equal(0, changed);
        available = false;
        await invites.CheckAsync(WorkPriority.Interactive, TestContext.Current.CancellationToken);
        Assert.Equal(2, ac.Total);
        Assert.Equal(0, changed);

        invites.Track(new DraftInviteEntry("E3", "S", "", now), null);
        Assert.Equal(3, ac.Total);
        Assert.Equal(1, changed);
        Assert.True(invites.Forget("E1"));
        Assert.Equal(2, ac.Total);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void DraftInvites_is_an_attention_source()
    {
        var invites = new DraftInvites(new ThrowingCalendar(), new DraftInvitesStore(Path.Combine(_dir, "x.json")),
            () => true, TimeZoneInfo.Utc);
        Assert.IsAssignableFrom<IAttentionSource>(invites);
    }
}
