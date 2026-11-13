namespace ClippysRevenge.Core.Tests;

public class BalloonTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (BalloonCenter C, ManualTimeProvider Clock, List<Balloon> Seen) Make()
    {
        var clock = new ManualTimeProvider();
        var c = new BalloonCenter(clock);
        var seen = new List<Balloon>();
        c.Shown += seen.Add;
        return (c, clock, seen);
    }

    [Fact]
    public void AiDrafted_texts()
    {
        Assert.Equal("It looks like you got mail. I drafted 1 reply — they're in your Drafts folder.", BalloonTexts.AiDrafted(1));
        Assert.Equal("It looks like you got mail. I drafted 3 replies — they're in your Drafts folder.", BalloonTexts.AiDrafted(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => BalloonTexts.AiDrafted(0));
    }

    [Fact]
    public void FirstPost()
    {
        var (c, _, seen) = Make();
        Assert.True(c.Post("hi"));
        Assert.Equal([new Balloon("hi", BalloonKind.Info)], seen);
    }

    [Fact]
    public void RateLimit()
    {
        var (c, clock, seen) = Make();
        Assert.True(c.Post("a"));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(c.Post("b"));
        Assert.Single(seen);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(c.Post("c"));
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void UserFeedback_NotRateLimited()
    {
        var (c, clock, seen) = Make();
        Assert.True(c.Post("a"));
        clock.Advance(TimeSpan.FromSeconds(1));
        c.PostUserFeedback("copied");
        Assert.Equal(2, seen.Count);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(c.Post("b"));

        var (c2, _, _) = Make();
        c2.PostUserFeedback("x");
        Assert.True(c2.Post("y"));
    }

    [Fact]
    public void InvalidText()
    {
        var (c, _, _) = Make();
        Assert.Throws<ArgumentNullException>(() => c.Post(null!));
        Assert.Throws<ArgumentException>(() => c.Post(""));
        Assert.Throws<ArgumentException>(() => c.Post("  "));
        Assert.Throws<ArgumentNullException>(() => c.PostUserFeedback(null!));
        Assert.Throws<ArgumentException>(() => c.PostUserFeedback(""));
        Assert.Throws<ArgumentException>(() => c.PostUserFeedback("  "));
    }

    [Fact]
    public void LongText()
    {
        var (c, _, seen) = Make();
        c.Post(new string('x', 400));
        Assert.Equal(255, seen[0].Text.Length);
        Assert.EndsWith("…", seen[0].Text);
    }

    [Fact]
    public void SubscriberErrors_Swallowed()
    {
        var (c, _, seen) = Make();
        var c2 = new BalloonCenter(new ManualTimeProvider());
        var second = 0;
        c2.Shown += _ => throw new InvalidOperationException();
        c2.Shown += _ => second++;
        Assert.True(c2.Post("x"));
        Assert.Equal(1, second);
        Assert.Empty(seen);
        Assert.NotNull(c);
    }

    [Fact]
    public void Concurrency_ExactlyOneAccepted()
    {
        var (c, _, _) = Make();
        var shown = 0;
        c.Shown += _ => Interlocked.Increment(ref shown);
        var accepted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (c.Post("x"))
            {
                Interlocked.Increment(ref accepted);
            }
        });
        Assert.Equal(1, accepted);
        Assert.Equal(1, shown);
    }

    private static readonly ActivityProgress NoProgress = ActivityProgress.None;

    [Fact]
    public void UnavailableEpisode()
    {
        var clock = new ManualTimeProvider();
        var p = new StateBalloonPolicy(clock);
        var s = ActivityState.OutlookUnavailable;
        Assert.Null(p.Observe(s, NoProgress, true));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Null(p.Observe(s, NoProgress, true));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new Balloon(BalloonTexts.OutlookUnavailable, BalloonKind.Warning), p.Observe(s, NoProgress, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(p.Observe(s, NoProgress, true));
    }

    [Fact]
    public void NotRespondingEpisode()
    {
        var clock = new ManualTimeProvider();
        var p = new StateBalloonPolicy(clock);
        var s = ActivityState.OutlookUnavailable;
        var progress = new ActivityProgress(0, 0, "not responding");
        Assert.Null(p.Observe(s, progress, true));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(new Balloon(BalloonTexts.OutlookNotResponding, BalloonKind.Warning), p.Observe(s, progress, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(p.Observe(s, progress, true));
    }

    [Fact]
    public void EpisodeReset()
    {
        var clock = new ManualTimeProvider();
        var p = new StateBalloonPolicy(clock);
        Assert.Null(p.Observe(ActivityState.OutlookUnavailable, NoProgress, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(p.Observe(ActivityState.Idle, NoProgress, true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(p.Observe(ActivityState.OutlookUnavailable, NoProgress, true));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Null(p.Observe(ActivityState.OutlookUnavailable, NoProgress, true));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(p.Observe(ActivityState.OutlookUnavailable, NoProgress, true));
    }

    [Fact]
    public void Error_NotificationsOn()
    {
        var clock = new ManualTimeProvider();
        var p = new StateBalloonPolicy(clock);
        var prog = new ActivityProgress(0, 0, "boom");
        Assert.Null(p.Observe(ActivityState.Error, prog, true));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(new Balloon(BalloonTexts.Error("boom"), BalloonKind.Error), p.Observe(ActivityState.Error, prog, true));
    }

    [Fact]
    public void Error_NotificationsOff_ThenOn()
    {
        var clock = new ManualTimeProvider();
        var p = new StateBalloonPolicy(clock);
        var prog = new ActivityProgress(0, 0, "boom");
        Assert.Null(p.Observe(ActivityState.Error, prog, false));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(p.Observe(ActivityState.Error, prog, false));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(p.Observe(ActivityState.Error, prog, false));
        Assert.NotNull(p.Observe(ActivityState.Error, prog, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(p.Observe(ActivityState.Error, prog, true));
    }

    [Theory]
    [InlineData(ActivityState.Idle)]
    [InlineData(ActivityState.Serving)]
    [InlineData(ActivityState.Paused)]
    [InlineData(ActivityState.Purging)]
    [InlineData(ActivityState.Syncing)]
    [InlineData(ActivityState.Backfilling)]
    [InlineData(ActivityState.AwaitingConfirmation)]
    public void OtherStates_Null(ActivityState state)
    {
        var clock = new ManualTimeProvider();
        var p = new StateBalloonPolicy(clock);
        Assert.Null(p.Observe(state, NoProgress, true));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(p.Observe(state, NoProgress, true));
    }

    [Fact]
    public void Texts()
    {
        Assert.Equal("Clippy's Revenge is running — right-click to copy the Claude setup command", BalloonTexts.FirstRun);
        Assert.Equal("It looks like Outlook isn't running. I'll wait — or right-click me and pick Start Outlook.", BalloonTexts.OutlookUnavailable);
        Assert.Equal("Copied — paste it in WSL", BalloonTexts.Copied);
        Assert.Equal("It looks like something went wrong: x. Right-click me and pick Activity… for details.", BalloonTexts.Error("x"));
        Assert.Equal("It looks like something went wrong. Right-click me and pick Activity… for details.", BalloonTexts.Error(""));
    }
}
