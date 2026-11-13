using System.Net;
using System.Text;

namespace ClippysRevenge.Core.Tests;

/// <summary>Scripted GitHub: per-URL queues of replies (the last one repeats), a request log, no network.</summary>
internal sealed class FakeGitHubHandler : HttpMessageHandler
{
    public sealed record Recorded(Uri Uri, Dictionary<string, string> Headers);

    /// <summary>One scripted response.</summary>
    public sealed class Reply
    {
        public int Status { get; init; } = 200;
        public string? Location { get; init; }
        public long? ContentLength { get; init; }
        public byte[] Body { get; init; } = [];

        /// <summary>0 = one piece.</summary>
        public int ChunkSize { get; init; }

        /// <summary>Awaited before each chunk (index 0, 1, ...).</summary>
        public Func<int, CancellationToken, Task>? BeforeChunk { get; init; }

        public bool Hang { get; init; }
        public Exception? Throw { get; init; }

        public static Reply Ok(byte[] body, int chunkSize = 0, Func<int, CancellationToken, Task>? beforeChunk = null,
            long? contentLength = null) =>
            new() { Body = body, ChunkSize = chunkSize, BeforeChunk = beforeChunk, ContentLength = contentLength };

        public static Reply Text(string text) => Ok(Encoding.UTF8.GetBytes(text));
        public static Reply Redirect(string location, int status = 302) => new() { Status = status, Location = location };
        public static Reply Code(int status) => new() { Status = status };
        public static Reply Hanging() => new() { Hang = true };
        public static Reply Throws(Exception ex) => new() { Throw = ex };
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Queue<Reply>> _map = new(StringComparer.Ordinal);
    private readonly List<Recorded> _requests = [];
    private readonly List<Uri> _unexpected = [];

    public FakeGitHubHandler On(string uri, params Reply[] replies)
    {
        lock (_lock)
        {
            var key = new Uri(uri).AbsoluteUri;
            if (!_map.TryGetValue(key, out var q))
            {
                _map[key] = q = new Queue<Reply>();
            }
            foreach (var r in replies) q.Enqueue(r);
        }
        return this;
    }

    public FakeGitHubHandler On(Uri uri, params Reply[] replies) => On(uri.AbsoluteUri, replies);

    public IReadOnlyList<Recorded> Requests
    {
        get { lock (_lock) return [.. _requests]; }
    }

    public IReadOnlyList<Uri> Unexpected
    {
        get { lock (_lock) return [.. _unexpected]; }
    }

    public int Count(Uri uri)
    {
        lock (_lock) return _requests.Count(r => r.Uri.AbsoluteUri == uri.AbsoluteUri);
    }

    public bool Requested(string uri)
    {
        var key = new Uri(uri).AbsoluteUri;
        lock (_lock) return _requests.Any(r => r.Uri.AbsoluteUri == key);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        Reply? reply = null;
        lock (_lock)
        {
            _requests.Add(new Recorded(uri, headers));
            if (_map.TryGetValue(uri.AbsoluteUri, out var q) && q.Count > 0)
            {
                reply = q.Count > 1 ? q.Dequeue() : q.Peek();
            }
            else
            {
                _unexpected.Add(uri);
            }
        }
        if (reply is null)
        {
            throw new HttpRequestException("unmapped URI in test");
        }
        ct.ThrowIfCancellationRequested();
        if (reply.Throw is not null) throw reply.Throw;
        if (reply.Hang)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        var response = new HttpResponseMessage((HttpStatusCode)reply.Status);
        if (reply.Location is not null)
        {
            response.Headers.TryAddWithoutValidation("Location", reply.Location);
        }
        if (reply.Status == 200)
        {
            HttpContent content = reply.ChunkSize > 0 || reply.BeforeChunk is not null
                ? new ChunkedContent(reply.Body, reply.ChunkSize > 0 ? reply.ChunkSize : reply.Body.Length,
                    reply.BeforeChunk)
                : new ByteArrayContent(reply.Body);
            content.Headers.ContentLength = reply.ContentLength ?? reply.Body.Length;
            response.Content = content;
        }
        return response;
    }

    private sealed class ChunkedContent(byte[] body, int chunk, Func<int, CancellationToken, Task>? before) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body, 0, body.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new ChunkStream(body, chunk, before));
    }

    private sealed class ChunkStream(byte[] body, int chunk, Func<int, CancellationToken, Task>? before) : Stream
    {
        private int _pos;
        private int _hooked = -1;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= body.Length) return 0;
            var index = _pos / chunk;
            if (index != _hooked)
            {
                if (before is not null) await before(index, ct).ConfigureAwait(false);
                _hooked = index;
            }
            var n = Math.Min(Math.Min(buffer.Length, (index + 1) * chunk - _pos), body.Length - _pos);
            body.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }
}

/// <summary>Shared fixtures for the update tests.</summary>
internal static class GitHubFixture
{
    public static readonly Uri Api = new(UpdateEndpoints.LatestReleaseApi);
    public static Uri ExeUrl(string tag = "v0.0.6") => UpdateEndpoints.AssetUrl(tag, UpdateEndpoints.ExeAssetName);
    public static Uri SumsUrl(string tag = "v0.0.6") => UpdateEndpoints.AssetUrl(tag, UpdateEndpoints.SumsAssetName);
    public const string Cdn = "https://release-assets.githubusercontent.com/x/y?sig=1";
    public const string Cdn2 = "https://release-assets.githubusercontent.com/x/z?sig=2";

    /// <summary>"MZ" + 1 MiB of seeded random bytes.</summary>
    public static byte[] Exe(int seed = 7)
    {
        var body = new byte[2 + 1_048_576];
        new Random(seed).NextBytes(body.AsSpan(2));
        body[0] = 0x4D;
        body[1] = 0x5A;
        return body;
    }

    public static string Sha(byte[] data) => UpdateHash.ToHex(System.Security.Cryptography.SHA256.HashData(data));

    public static string Sums(byte[] exe) => Sha(exe) + " *ClippysRevenge.exe\n";

    public static string ReleaseJson(string tag = "v0.0.6", long exeSize = 1_048_578, string? digest = null,
        bool prerelease = false)
    {
        var digestPart = digest is null ? "" : $",\"digest\":\"sha256:{digest}\"";
        return $$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"published_at":"2026-11-19T10:00:00Z",
             "html_url":"https://evil.example/html",
             "assets":[
              {"name":"ClippysRevenge.exe","state":"uploaded","size":{{exeSize}}{{digestPart}},"browser_download_url":"https://evil.example/exe"},
              {"name":"SHA256SUMS.txt","state":"uploaded","size":100,"browser_download_url":"https://evil.example/sums"}]}
            """;
    }

    public static ReleaseInfo Release(byte[] exe, string tag = "v0.0.6", string? digest = null)
    {
        var parsed = ReleaseParser.Parse(ReleaseJson(tag, exe.Length, digest));
        return parsed.Release ?? throw new InvalidOperationException(parsed.Error);
    }

    /// <summary>Scripts the whole happy path for a release: sums and exe, each behind a CDN redirect.</summary>
    public static void ScriptDownload(FakeGitHubHandler h, byte[] exe, string tag = "v0.0.6",
        Func<int, CancellationToken, Task>? beforeChunk = null, int chunk = 0)
    {
        h.On(SumsUrl(tag), FakeGitHubHandler.Reply.Redirect(Cdn));
        h.On(Cdn, FakeGitHubHandler.Reply.Text(Sums(exe)));
        h.On(ExeUrl(tag), FakeGitHubHandler.Reply.Redirect(Cdn2));
        h.On(Cdn2, FakeGitHubHandler.Reply.Ok(exe, chunk, beforeChunk));
    }

    /// <summary>Polls (real time, short) until the condition holds; fails after 10 s.</summary>
    public static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(5);
        }
    }

    public sealed class SyncProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
}
