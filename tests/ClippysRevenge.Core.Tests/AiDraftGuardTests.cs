namespace ClippysRevenge.Core.Tests;

public class AiDraftGuardTests
{
    private static readonly string FP = new('a', 64);
    private static readonly AiDraftInfo Base = new(true, false, "ai", "IPM.Note", false, FP, "Re:: Ski", "x");

    private static ObsoleteDraftStatus E(AiDraftInfo? i, string? fp = null) => AiDraftGuard.Evaluate(i, fp ?? FP);

    public static TheoryData<AiDraftInfo?, string, ObsoleteDraftStatus> Cases() => new()
    {
        { Base, FP, ObsoleteDraftStatus.Movable },
        { null, FP, ObsoleteDraftStatus.Gone },
        { Base with { InDrafts = false }, FP, ObsoleteDraftStatus.NotInDrafts },
        { Base with { Sent = true }, FP, ObsoleteDraftStatus.Sent },
        { Base with { Marker = "tool" }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { Marker = null }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { Marker = "x" }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { Marker = " ai" }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { Marker = "AI" }, FP, ObsoleteDraftStatus.Movable },
        { Base with { MessageClass = "IPM.Note.SMIME" }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { MessageClass = "IPM.Schedule.Meeting.Request" }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { MessageClass = null }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { MessageClass = "ipm.note" }, FP, ObsoleteDraftStatus.Movable },
        { Base with { Open = true }, FP, ObsoleteDraftStatus.Open },
        { Base, new string('b', 64), ObsoleteDraftStatus.Changed },
        { Base with { Fingerprint = "" }, "", ObsoleteDraftStatus.Changed },
        { Base with { Fingerprint = FP.ToUpperInvariant() }, FP, ObsoleteDraftStatus.Changed },
        { Base with { InDrafts = false, Sent = true, Marker = "x", Open = true }, new string('b', 64), ObsoleteDraftStatus.NotInDrafts },
        { Base with { Sent = true, Marker = "x" }, FP, ObsoleteDraftStatus.Sent },
        { Base with { Marker = "x", Open = true }, FP, ObsoleteDraftStatus.NotMarked },
        { Base with { Open = true }, new string('b', 64), ObsoleteDraftStatus.Open },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Evaluate(AiDraftInfo? info, string expected, ObsoleteDraftStatus status)
    {
        var r = AiDraftGuard.Evaluate(info, expected);
        Assert.Equal(status, r);
        Assert.NotEqual(ObsoleteDraftStatus.Moved, r);
        Assert.NotEqual(ObsoleteDraftStatus.NoDeletedItems, r);
    }

    [Fact]
    public void Null_expected_throws() =>
        Assert.Throws<ArgumentNullException>("expectedFingerprint", () => AiDraftGuard.Evaluate(Base, null!));

    [Fact]
    public async Task Fake_default_move()
    {
        var f = new FakeAiMailbox { DraftInfo = (_, _) => FakeAiMailbox.Draft(FP) };
        var r = await f.MoveObsoleteAiDraftAsync("", "D1", FP, WorkPriority.Background, CancellationToken.None);
        Assert.Equal(new ObsoleteDraftMove(ObsoleteDraftStatus.Moved, "Re:: Budget"), r);
        Assert.Contains("D1", f.MovedIds);
        var read = await f.ReadAiDraftAsync("", "D1", WorkPriority.Background, CancellationToken.None);
        Assert.False(read!.InDrafts);
        Assert.Equal(ObsoleteDraftStatus.NotInDrafts, AiDraftGuard.Evaluate(read, FP));
        Assert.Equal(("", "D1", FP, WorkPriority.Background), f.Moves.Single());
        Assert.Equal(("", "D1", WorkPriority.Background), f.DraftReads.Single());
    }

    [Fact]
    public async Task Fake_changed_gone_and_throw()
    {
        var f = new FakeAiMailbox { DraftInfo = (_, _) => FakeAiMailbox.Draft(new string('c', 64), "S") };
        var r = await f.MoveObsoleteAiDraftAsync("", "D1", FP, WorkPriority.Background, CancellationToken.None);
        Assert.Equal(new ObsoleteDraftMove(ObsoleteDraftStatus.Changed, "S"), r);
        Assert.Empty(f.MovedIds);

        var g = new FakeAiMailbox();
        Assert.Equal(new ObsoleteDraftMove(ObsoleteDraftStatus.Gone, ""),
            await g.MoveObsoleteAiDraftAsync("", "D1", FP, WorkPriority.Background, CancellationToken.None));

        g.MoveThrow = new InvalidOperationException();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            g.MoveObsoleteAiDraftAsync("", "D1", FP, WorkPriority.Background, CancellationToken.None));
        Assert.Equal(2, g.Moves.Count);
    }
}
