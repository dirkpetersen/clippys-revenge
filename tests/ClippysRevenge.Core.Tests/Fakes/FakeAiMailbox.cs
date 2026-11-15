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

    private readonly List<(string Mailbox, string EntryId, WorkPriority Priority)> _draftReads = [];
    private readonly List<(string Mailbox, string EntryId, string Fingerprint, WorkPriority Priority)> _moves = [];
    private readonly HashSet<string> _movedIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _draftPerId = new(StringComparer.Ordinal);

    /// <summary>(draftEntryId, 1-based read number for that id) to the info; null = gone. Default: always null.</summary>
    public Func<string, int, AiDraftInfo?> DraftInfo { get; set; } = (_, _) => null;
    public Exception? DraftReadThrow { get; set; }

    /// <summary>Null = default: read DraftInfo (counts as a read of that id), Evaluate, Movable gives Moved and remembers the id.</summary>
    public Func<string, string, ObsoleteDraftMove>? MoveBehavior { get; set; }
    public Exception? MoveThrow { get; set; }

    public IReadOnlyList<(string Mailbox, string EntryId, WorkPriority Priority)> DraftReads
    {
        get { lock (_lock) return _draftReads.ToArray(); }
    }

    public IReadOnlyList<(string Mailbox, string EntryId, string Fingerprint, WorkPriority Priority)> Moves
    {
        get { lock (_lock) return _moves.ToArray(); }
    }

    public IReadOnlyCollection<string> MovedIds
    {
        get { lock (_lock) return _movedIds.ToArray(); }
    }

    /// <summary>A Movable info: InDrafts, not sent, marker "ai", "IPM.Note", not open.</summary>
    public static AiDraftInfo Draft(string fingerprint, string subject = "Re:: Budget", string body = "Old draft text.") =>
        new(true, false, "ai", "IPM.Note", false, fingerprint, subject, body);

    private AiDraftInfo? ReadInfo(string id)
    {
        int n;
        lock (_lock) _draftPerId[id] = n = _draftPerId.GetValueOrDefault(id) + 1;
        var info = DraftInfo(id, n);
        bool moved;
        lock (_lock) moved = _movedIds.Contains(id);
        return moved && info is not null ? info with { InDrafts = false } : info;
    }

    public Task<AiDraftInfo?> ReadAiDraftAsync(string mailbox, string draftEntryId, WorkPriority priority, CancellationToken ct)
    {
        lock (_lock)
        {
            TotalCalls++;
            _draftReads.Add((mailbox, draftEntryId, priority));
        }
        if (DraftReadThrow is not null) throw DraftReadThrow;
        return Task.FromResult(ReadInfo(draftEntryId));
    }

    public Task<ObsoleteDraftMove> MoveObsoleteAiDraftAsync(string mailbox, string draftEntryId, string expectedFingerprint,
        WorkPriority priority, CancellationToken ct)
    {
        lock (_lock)
        {
            TotalCalls++;
            _moves.Add((mailbox, draftEntryId, expectedFingerprint, priority));
        }
        if (MoveThrow is not null) throw MoveThrow;
        if (MoveBehavior is not null) return Task.FromResult(MoveBehavior(draftEntryId, expectedFingerprint));

        var info = ReadInfo(draftEntryId);
        var status = AiDraftGuard.Evaluate(info, expectedFingerprint);
        if (status == ObsoleteDraftStatus.Movable)
        {
            lock (_lock) _movedIds.Add(draftEntryId);
            return Task.FromResult(new ObsoleteDraftMove(ObsoleteDraftStatus.Moved, info!.Subject));
        }
        return Task.FromResult(new ObsoleteDraftMove(status, info?.Subject ?? ""));
    }
}
