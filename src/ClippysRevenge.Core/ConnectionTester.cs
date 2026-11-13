using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ClippysRevenge.Core;

public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>
/// Settings "Test connection" (SPEC 6.3): two MCP initialize requests to the running server, without and with
/// the token. Talks to 127.0.0.1 only, never sends Origin, never puts the token or a response body in a message.
/// </summary>
public sealed class ConnectionTester : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    public const int MaxResponseBytes = 64 * 1024;

    private const string Body =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"clippy-settings","version":"1"}}}""";

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <summary>handler null: new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }.</summary>
    public ConnectionTester(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _timeout = timeout ?? DefaultTimeout;
    }

    public void Dispose() => _http.Dispose();

    private sealed record Reply(int Status, string? MediaType, string Body);

    /// <summary>Never throws except OperationCanceledException for ct. port null = server not running.</summary>
    /// <exception cref="ArgumentException">token is null or empty.</exception>
    public async Task<ConnectionTestResult> TestAsync(int? port, string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (port is not { } p)
            return Fail("The server isn't running. Check the tray icon for the reason.");
        var ps = p.ToString(CultureInfo.InvariantCulture);
        var url = $"http://127.0.0.1:{ps}/mcp";
        try
        {
            var first = await PostAsync(url, null, readBody: false, ct).ConfigureAwait(false);
            if (first.Status != 401)
                return Fail($"The server answered without a token (HTTP {first.Status.ToString(CultureInfo.InvariantCulture)}). Another program may be using port {ps}.");

            var second = await PostAsync(url, token, readBody: true, ct).ConfigureAwait(false);
            var code = second.Status.ToString(CultureInfo.InvariantCulture);
            if (second.Status == 401) return Fail("The server rejected the token (HTTP 401).");
            if (second.Status != 200) return Fail($"The server answered HTTP {code} to initialize.");
            var name = ReadServerName(second.MediaType, second.Body);
            if (name is null) return Fail("The server answered, but not with an MCP initialize result.");
            if (name != McpHttpHost.ServerName)
                return Fail($"Another MCP server ({Clean(name)}) is answering on port {ps}.");
            return new ConnectionTestResult(true, $"✓ Connection works: 127.0.0.1:{ps} needs the token and answers MCP.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var secs = ((int)Math.Ceiling(_timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Fail($"The server on 127.0.0.1:{ps} didn't answer within {secs} s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Fail($"Nothing answers on 127.0.0.1:{ps}.");
        }
    }

    private static ConnectionTestResult Fail(string message) => new(false, "✗ " + message);

    private static string Clean(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            if (!char.IsControl(c)) sb.Append(c);
        return sb.Length > 40 ? sb.ToString(0, 40) : sb.ToString();
    }

    private async Task<Reply> PostAsync(string url, string? token, bool readBody, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var media = resp.Content.Headers.ContentType?.MediaType;
        if (!readBody) return new Reply(status, media, "");

        var sse = string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        while (ms.Length < MaxResponseBytes)
        {
            var n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, MaxResponseBytes - ms.Length)), cts.Token).ConfigureAwait(false);
            if (n == 0) break;
            ms.Write(buf, 0, n);
            if (sse && HasFirstEvent(ms)) break;
        }
        return new Reply(status, media, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }

    private static bool HasFirstEvent(MemoryStream ms)
    {
        var s = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        return s.Contains("\n\n", StringComparison.Ordinal) || s.Contains("\r\n\r\n", StringComparison.Ordinal);
    }

    /// <summary>result.serverInfo.name from a JSON body or the first SSE event's data; null if absent/malformed.</summary>
    internal static string? ReadServerName(string? mediaType, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var json = body;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var data = new List<string>();
            foreach (var raw in body.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    if (data.Count > 0) break;
                    continue;
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var v = line[5..];
                if (v.StartsWith(' ')) v = v[1..];
                data.Add(v);
            }
            if (data.Count == 0) return null;
            json = string.Join('\n', data);
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
