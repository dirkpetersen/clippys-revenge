using System.Net;
using System.Text;
using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

/// <summary>Scripted HttpMessageHandler that records requests. No network.</summary>
internal sealed class FakeAnthropicHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body);

    public const string Reply = """{"needs_reply":true,"confidence":0.8,"reason":"x","body_html":"<p>Hi</p>"}""";

    private readonly Lock _lock = new();
    private readonly List<Recorded> _requests = [];
    private int _calls;

    public Func<Recorded, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } =
        (_, _) => Task.FromResult(Json(200, OkBody(Reply)));

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<Recorded> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    public Recorded Last
    {
        get { lock (_lock) return _requests[^1]; }
    }

    public static string OkBody(string text, string stop = "end_turn", long inTok = 120, long outTok = 45) =>
        OkContent($$"""[{"type":"text","text":{{JsonSerializer.Serialize(text)}}}]""", stop, inTok, outTok);

    public static string OkContent(string contentJson, string stop = "end_turn", long inTok = 120, long outTok = 45) =>
        $$$"""{"id":"msg_01","type":"message","role":"assistant","model":"claude-opus-5-5","content":{{{contentJson}}},"stop_reason":"{{{stop}}}","stop_sequence":null,"usage":{"input_tokens":{{{inTok}}},"output_tokens":{{{outTok}}}}}""";

    public static HttpResponseMessage Json(int status, string body, params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (n, v) in headers) r.Headers.TryAddWithoutValidation(n, v);
        return r;
    }

    public FakeAnthropicHandler Respond(int status, string body, params (string Name, string Value)[] headers)
    {
        Responder = (_, _) => Task.FromResult(Json(status, body, headers));
        return this;
    }

    public FakeAnthropicHandler Throw()
    {
        Responder = (_, _) => throw new HttpRequestException("boom");
        return this;
    }

    public FakeAnthropicHandler Hang()
    {
        Responder = async (_, ct) =>
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(200, "{}");
        };
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var rec = new Recorded(request.Method, request.RequestUri!, headers, body);
        lock (_lock) _requests.Add(rec);
        Interlocked.Increment(ref _calls);
        return await Responder(rec, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
