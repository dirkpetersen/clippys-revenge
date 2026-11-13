using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

/// <summary>Scripted IDraftModel: a queue of results, or an OnRun hook. Records every request.</summary>
internal sealed class FakeDraftModel : IDraftModel
{
    private readonly Lock _lock = new();
    private readonly Queue<ModelResult> _queue = new();
    private readonly List<ModelRequest> _requests = [];

    public ModelStatus Status { get; set; } = new(true, "✓ ready");

    /// <summary>When set, runs instead of the queue (request, 1-based call number).</summary>
    public Func<ModelRequest, int, Task<ModelResult>>? OnRun { get; set; }

    public int CheckCalls { get; private set; }

    public IReadOnlyList<ModelRequest> Requests
    {
        get { lock (_lock) return _requests.ToArray(); }
    }

    public void Enqueue(params ModelResult[] results)
    {
        lock (_lock)
            foreach (var r in results) _queue.Enqueue(r);
    }

    public Task<ModelStatus> CheckAsync(CancellationToken ct)
    {
        lock (_lock) CheckCalls++;
        return Task.FromResult(Status);
    }

    public Task<ModelResult> RunAsync(ModelRequest request, CancellationToken ct)
    {
        int n;
        lock (_lock)
        {
            _requests.Add(request);
            n = _requests.Count;
        }
        if (OnRun is not null) return OnRun(request, n);
        lock (_lock)
            return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : new ModelResult.Failed(ModelFailure.BadInput, null));
    }

    public static ModelResult Ok(string json) =>
        new ModelResult.Ok(JsonDocument.Parse(json).RootElement.Clone(), TimeSpan.FromSeconds(1), 10, 5);

    public static ModelResult OkReply(double confidence, bool replyAll = false, bool needsReply = true,
        string body = "<p>Hi [[date]]</p><script>x</script>")
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["needs_reply"] = needsReply,
            ["confidence"] = confidence,
            ["reply_all"] = replyAll,
            ["body_html"] = body,
            ["reason"] = "SECRET-REASON",
        });
        return Ok(json);
    }
}
