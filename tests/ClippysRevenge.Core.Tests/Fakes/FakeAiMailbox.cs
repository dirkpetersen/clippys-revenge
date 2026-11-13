namespace ClippysRevenge.Core.Tests;

/// <summary>In-memory IAiMailbox. Facts default to a plain IPM.Note with no headers, verb or draft.</summary>
internal sealed class FakeAiMailbox : IAiMailbox
{
    private readonly Lock _lock = new();
    private readonly List<(string Mailbox, string EntryId, WorkPriority Priority)> _factCalls = [];
    private readonly Dictionary<string, int> _perId = new(StringComparer.Ordinal);

    /// <summary>(entryId, 1-based call number for that id) to facts. Null result = the item is gone.</summary>
    public Func<string, int, CandidateFacts?> Facts { get; set; } = (_, _) => DefaultFacts;

    public static CandidateFacts DefaultFacts { get; } = new("IPM.Note", "<imid@x>", "Budget", null, null, false);

    public Exception? FactsThrow { get; set; }
    public AiIdentity Identity { get; set; } = new("Dirk Example", "me@example.org");
    public Exception? IdentityThrow { get; set; }
    public SelectedEmail? Selected { get; set; }
    public Exception? SelectedThrow { get; set; }
    public LiveEmail? Live { get; set; }
    public Exception? ReadThrow { get; set; }
    public bool ShowResult { get; set; } = true;
    public int IdentityCalls { get; private set; }
    public int TotalCalls { get; private set; }

    public IReadOnlyList<(string Mailbox, string EntryId, WorkPriority Priority)> FactCalls
    {
        get { lock (_lock) return _factCalls.ToArray(); }
    }

    public Task<CandidateFacts?> GetFactsAsync(string mailbox, string entryId, WorkPriority priority, CancellationToken ct)
    {
        int n;
        lock (_lock)
        {
            TotalCalls++;
            _factCalls.Add((mailbox, entryId, priority));
            _perId[entryId] = n = _perId.GetValueOrDefault(entryId) + 1;
        }
        if (FactsThrow is not null) throw FactsThrow;
        return Task.FromResult(Facts(entryId, n));
    }

    public Task<AiIdentity> GetIdentityAsync(WorkPriority priority, CancellationToken ct)
    {
        lock (_lock) { TotalCalls++; IdentityCalls++; }
        if (IdentityThrow is not null) throw IdentityThrow;
        return Task.FromResult(Identity);
    }

    public Task<SelectedEmail?> GetSelectedAsync(CancellationToken ct)
    {
        lock (_lock) TotalCalls++;
        if (SelectedThrow is not null) throw SelectedThrow;
        return Task.FromResult(Selected);
    }

    public Task<LiveEmail?> ReadEmailAsync(string entryId, CancellationToken ct)
    {
        lock (_lock) TotalCalls++;
        if (ReadThrow is not null) throw ReadThrow;
        return Task.FromResult(Live);
    }

    public Task<bool> ShowDraftsAsync(CancellationToken ct)
    {
        lock (_lock) TotalCalls++;
        return Task.FromResult(ShowResult);
    }
}
