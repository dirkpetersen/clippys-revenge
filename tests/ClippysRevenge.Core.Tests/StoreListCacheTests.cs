using Xunit;

namespace ClippysRevenge.Core.Tests;

public class StoreListCacheTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly StoreCandidate Own = new("Me", "S0", "me@x.edu", 0, true);
    private static readonly StoreCandidate ShA = new("Helpdesk", "S1", null, 4, false);
    private static readonly StoreCandidate ShB = new("RO", "S2", null, 4, false);

    private static (StoreListCache C, ManualTimeProvider T) Make()
    {
        var t = new ManualTimeProvider();
        return (new StoreListCache(t), t);
    }

    [Fact]
    public void NewCacheHasNothing()
    {
        var (c, _) = Make();
        Assert.False(c.TryGet(1, out _));
    }

    [Fact]
    public void SetThenGet()
    {
        var (c, _) = Make();
        c.Set(1, [Own, ShA]);
        Assert.True(c.TryGet(1, out var s));
        Assert.Equal(2, s!.Count);
        Assert.Equal("", s.Single(x => x.StoreId == "S1").Smtp);
        Assert.False(c.TryGet(2, out _));
    }

    [Fact]
    public void ExpiresAfterTtl()
    {
        var (c, t) = Make();
        c.Set(1, [Own, ShA]);
        t.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.False(c.TryGet(1, out _));
    }

    [Fact]
    public void LookupAppliedAndRemembered()
    {
        var (c, _) = Make();
        c.Set(1, [Own, ShA]);
        c.RecordLookup(1, "S1", "Helpdesk@X.EDU");
        Assert.True(c.TryGet(1, out var s));
        Assert.Equal("helpdesk@x.edu", s!.Single(x => x.StoreId == "S1").Smtp);
        var s2 = c.Set(2, [Own, ShA]);
        Assert.Equal("helpdesk@x.edu", s2.Single(x => x.StoreId == "S1").Smtp);
    }

    [Fact]
    public void CandidateSmtpWins()
    {
        var (c, _) = Make();
        c.Set(1, [ShA]);
        c.RecordLookup(1, "S1", "a@x.edu");
        var s = c.Set(2, [ShA with { Smtp = "b@x.edu" }]);
        Assert.Equal("b@x.edu", s[0].Smtp);
    }

    [Fact]
    public void PendingLookups()
    {
        var (c, _) = Make();
        c.Set(1, [Own, ShA, ShB]);
        string[] key = ["helpdesk@x.edu"];
        Assert.Equal(["S1", "S2"], c.PendingLookups(1, key, false));
        c.RecordLookup(1, "S1", "helpdesk@x.edu");
        Assert.Empty(c.PendingLookups(1, key, false));
        Assert.Equal(["S2"], c.PendingLookups(1, key, true));
        c.RecordLookup(1, "S2", null);
        Assert.Empty(c.PendingLookups(1, key, true));
        c.Set(2, [Own, ShA, ShB]);
        Assert.Equal(["S2"], c.PendingLookups(2, key, true));
    }

    [Fact]
    public void TimeoutCooldown()
    {
        var (c, t) = Make();
        c.Set(1, [Own, ShB]);
        c.RecordTimeout("S2");
        Assert.Empty(c.PendingLookups(1, [], true));
        c.Set(2, [Own, ShB]);
        Assert.Empty(c.PendingLookups(2, [], true));
        t.Advance(StoreListCache.TimeoutCooldown + TimeSpan.FromSeconds(1));
        c.Set(3, [Own, ShB]);
        Assert.Equal(["S2"], c.PendingLookups(3, [], true));
    }

    [Fact]
    public void NoListMeansNotSettled()
    {
        var (c, _) = Make();
        Assert.Empty(c.PendingLookups(1, [], true));
        Assert.False(c.IsSettled(1, [], false));
        c.Set(1, [Own]);
        Assert.True(c.IsSettled(1, [], false));
    }

    [Fact]
    public void InvalidateKeepsKnownSmtps()
    {
        var (c, _) = Make();
        c.Set(1, [Own, ShA]);
        c.RecordLookup(1, "S1", "h@x.edu");
        c.Invalidate();
        Assert.False(c.TryGet(1, out _));
        Assert.Equal("h@x.edu", c.Set(1, [Own, ShA]).Single(x => x.StoreId == "S1").Smtp);
    }

    [Fact]
    public async Task ConcurrentUse()
    {
        var (c, _) = Make();
        var tasks = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                switch ((i + n) % 3)
                {
                    case 0: c.RecordLookup(i % 3, "S1", i % 2 == 0 ? "a@x.edu" : null); break;
                    case 1: _ = c.TryGet(i % 3, out _); break;
                    default: c.Set(i % 3, [Own, ShA, ShB]); break;
                }
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);
    }
}
