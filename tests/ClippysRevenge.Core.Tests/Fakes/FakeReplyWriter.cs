namespace ClippysRevenge.Core.Tests;

/// <summary>Records every ReplyPlan; returns RepliedDraft("D1", "Re:: Budget", "") unless Behavior says otherwise.</summary>
internal sealed class FakeReplyWriter : IReplyDraftWriter
{
    private readonly Lock _lock = new();
    private readonly List<ReplyPlan> _plans = [];

    /// <summary>(plan, 1-based call number). Throw to fail.</summary>
    public Func<ReplyPlan, int, RepliedDraft> Behavior { get; set; } = (_, _) => new RepliedDraft("D1", "Re:: Budget", "");

    public IReadOnlyList<ReplyPlan> Plans
    {
        get { lock (_lock) return _plans.ToArray(); }
    }

    public Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct)
    {
        int n;
        lock (_lock)
        {
            _plans.Add(plan);
            n = _plans.Count;
        }
        return Task.FromResult(Behavior(plan, n));
    }
}
