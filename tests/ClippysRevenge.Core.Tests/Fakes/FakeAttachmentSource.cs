using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

/// <summary>Scripted IAttachmentSource: (entryId, position) gives a result or an exception; gone entries give null.</summary>
internal sealed class FakeAttachmentSource : IAttachmentSource
{
    private readonly Dictionary<(string, int), Func<AiAttachmentRead?>> _script = [];
    private readonly HashSet<string> _gone = new(StringComparer.Ordinal);
    private int _inFlight;

    public List<string> Log { get; } = [];
    public IReadOnlyList<SyncedRoot> Roots { get; set; } = [];
    public int RootsCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public int MaxInFlight { get; private set; }
    public Action? OnRead { get; set; }
    public Exception? RootsError { get; set; }

    public FakeAttachmentSource Returns(string entryId, int position, AiAttachmentRead? read)
    {
        _script[(entryId, position)] = () => read;
        return this;
    }

    public FakeAttachmentSource Throws(string entryId, int position, Exception e)
    {
        _script[(entryId, position)] = () => throw e;
        return this;
    }

    public FakeAttachmentSource Gone(string entryId)
    {
        _gone.Add(entryId);
        return this;
    }

    public IReadOnlyList<SyncedRoot> SyncedRoots()
    {
        RootsCalls++;
        if (RootsError is not null) throw RootsError;
        return Roots;
    }

    public async Task<AiAttachmentRead?> ReadAsync(string mailbox, string entryId, AttachmentInfo wanted, long maxBytes,
        WorkPriority priority, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inFlight);
        try
        {
            if (now > 1) throw new InvalidOperationException("overlapping ReadAsync calls");
            MaxInFlight = Math.Max(MaxInFlight, now);
            ReadCalls++;
            lock (Log) Log.Add($"read {entryId}/{wanted.Position}");
            OnRead?.Invoke();
            await Task.Yield();
            if (_gone.Contains(entryId)) return null;
            return _script.TryGetValue((entryId, wanted.Position), out var f) ? f() : null;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
