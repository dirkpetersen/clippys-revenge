using System.Net;
using System.Net.Http.Headers;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core.Tests;

public partial class McpHttpHostTests
{
    private const string Bearer = "Bearer test-token-1";

    private sealed class LinkEnv : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "clippy-files-" + Guid.NewGuid().ToString("N"));
        public AttachmentLinks Links { get; }

        public LinkEnv(TimeProvider? time = null)
        {
            Links = new AttachmentLinks(new AttachmentLinksOptions
            {
                Folder = Dir,
                EnsurePrivateFolder = d => Directory.CreateDirectory(d),
                Time = time ?? TimeProvider.System,
            });
        }

        public AttachmentLink Add(byte[] bytes, string name = "report.pdf")
        {
            var p = Links.PrepareTempPath();
            File.WriteAllBytes(p, bytes);
            return Links.Add(p, name, bytes.Length);
        }

        public void Dispose()
        {
            Links.Dispose();
            try
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i % 251);
        return b;
    }

    private static async Task<HttpResponseMessage> Send(int port, string path, string method = "GET", string? auth = Bearer,
        string? origin = null, string? range = null, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead,
        HttpClient? client = null)
    {
        var http = client ?? new HttpClient();
        try
        {
            var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
            if (auth is not null) req.Headers.TryAddWithoutValidation("Authorization", auth);
            if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
            if (range is not null) req.Headers.TryAddWithoutValidation("Range", range);
            return await http.SendAsync(req, option, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (client is null) http.Dispose();
        }
    }

    private static string Header(HttpResponseMessage r, string name)
    {
        if (r.Headers.NonValidated.TryGetValues(name, out var v) || r.Content.Headers.NonValidated.TryGetValues(name, out v))
        {
            return string.Join(",", v);
        }
        return "";
    }

    private static bool HasHeader(HttpResponseMessage r, string name) =>
        r.Headers.NonValidated.Contains(name) || r.Content.Headers.NonValidated.Contains(name);

    private static async Task AssertEmpty404(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static int ToolEvents(ActivityHub hub) => hub.GetEvents().Count(e => e.Kind == ActivityKind.Tool);

    private sealed class CaptureProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, string Text);

        private readonly Lock _lock = new();
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_lock) return _entries.ToArray(); }
        }

        public ILogger CreateLogger(string categoryName) => new Cap(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Cap(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var text = formatter(state, exception) + "|" + exception;
                lock (owner._lock) owner._entries.Add(new Entry(category, logLevel, text));
            }
        }
    }

    [Fact]
    public async Task Get_200_exact_headers_and_bytes()
    {
        using var env = new LinkEnv();
        var bytes = Pattern(256_000);
        var link = env.Add(bytes);
        await using var f = await Started(links: env.Links);
        using var r = await Send(f.Port, "/files/" + link.Token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/octet-stream", Header(r, "Content-Type"));
        Assert.Equal("attachment; filename=\"report.pdf\"; filename*=UTF-8''report.pdf", Header(r, "Content-Disposition"));
        Assert.Equal("256000", Header(r, "Content-Length"));
        Assert.Equal("nosniff", Header(r, "X-Content-Type-Options"));
        Assert.Equal("no-store", Header(r, "Cache-Control"));
        Assert.Equal(bytes, await r.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        foreach (var h in new[] { "Accept-Ranges", "ETag", "Last-Modified", "Content-Range", "Server" })
        {
            Assert.False(HasHeader(r, h), h);
        }
    }

    [Fact]
    public async Task Get_non_ascii_name_uses_build_values()
    {
        using var env = new LinkEnv();
        const string name = "Q3 Bericht ü \"x\".pdf";
        var bytes = Pattern(1234);
        var link = env.Add(bytes, name);
        await using var f = await Started(links: env.Links);
        using var r = await Send(f.Port, "/files/" + link.Token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        foreach (var (k, v) in FileDownloadHeaders.Build(name, bytes.Length))
        {
            Assert.Equal(v, Header(r, k));
        }
    }

    [Fact]
    public async Task Second_get_404_empty()
    {
        using var env = new LinkEnv();
        var link = env.Add(Pattern(1000));
        await using var f = await Started(links: env.Links);
        using (var r1 = await Send(f.Port, "/files/" + link.Token))
        {
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            await r1.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        using var r2 = await Send(f.Port, "/files/" + link.Token);
        await AssertEmpty404(r2);
        Assert.Equal("0", Header(r2, "Content-Length"));
    }

    [Fact]
    public async Task File_deleted_after_download()
    {
        using var env = new LinkEnv();
        var link = env.Add(Pattern(5000));
        await using var f = await Started(links: env.Links);
        using (var r = await Send(f.Port, "/files/" + link.Token))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            await r.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        Assert.True(await WaitFor(() => !File.Exists(link.TempPath)));
        Assert.Equal(0, env.Links.OpenCount);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("abc")]
    [InlineData("dot")]
    [InlineData("long")]
    [InlineData("%2E%2E")]
    [InlineData("extra")]
    public async Task Unknown_and_malformed_tokens_404(string kind)
    {
        using var env = new LinkEnv();
        var link = env.Add(Pattern(10));
        var path = kind switch
        {
            "new" => AttachmentLinks.NewToken(),
            "dot" => new string('a', 42) + ".",
            "long" => new string('a', 44),
            "extra" => link.Token + "/x",
            _ => kind,
        };
        await using var f = await Started(links: env.Links);
        var before = f.Hub.GetEvents().Count;
        using var r = await Send(f.Port, "/files/" + path);
        await AssertEmpty404(r);
        Assert.Equal(before, f.Hub.GetEvents().Count);
        Assert.Equal(1, env.Links.OpenCount);
    }

    [Theory]
    [InlineData("/files")]
    [InlineData("/files/")]
    public async Task Bare_files_paths_404(string path)
    {
        using var env = new LinkEnv();
        await using var f = await Started(links: env.Links);
        using var r = await Send(f.Port, path);
        await AssertEmpty404(r);
    }

    [Fact]
    public async Task Expired_404_and_file_deleted()
    {
        var time = new ManualTimeProvider();
        using var env = new LinkEnv(time);
        var link = env.Add(Pattern(100));
        await using var f = await Started(links: env.Links);
        time.Advance(AttachmentLinks.LinkLifetime + TimeSpan.FromSeconds(1));
        using var r = await Send(f.Port, "/files/" + link.Token);
        await AssertEmpty404(r);
        Assert.True(await WaitFor(() => !File.Exists(link.TempPath)));
    }

    [Fact]
    public async Task No_bearer_401_link_still_works()
    {
        using var env = new LinkEnv();
        var bytes = Pattern(3000);
        var link = env.Add(bytes);
        var path = "/files/" + link.Token;
        await using var f = await Started(links: env.Links);
        using (var r = await Send(f.Port, path, auth: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Empty(r.Headers.WwwAuthenticate);
        }
        using (var r = await Send(f.Port, path, auth: "Bearer wrong"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        using (var r = await Send(f.Port, path, origin: "http://evil.example"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
        using var ok = await Send(f.Port, path);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(bytes, await ok.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Other_methods_405_link_still_works(string method)
    {
        using var env = new LinkEnv();
        var link = env.Add(Pattern(500));
        var path = "/files/" + link.Token;
        await using var f = await Started(links: env.Links);
        using (var r = await Send(f.Port, path, method))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
            Assert.Equal("", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("GET", Header(r, "Allow"));
        }
        using var ok = await Send(f.Port, path);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(500, (await ok.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Range_ignored()
    {
        using var env = new LinkEnv();
        var link = env.Add(Pattern(2000));
        await using var f = await Started(links: env.Links);
        using var r = await Send(f.Port, "/files/" + link.Token, range: "bytes=0-9");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("2000", Header(r, "Content-Length"));
        Assert.False(HasHeader(r, "Content-Range"));
        Assert.Equal(2000, (await r.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Missing_temp_file_404_and_used_up()
    {
        using var env = new LinkEnv();
        var link = env.Add(Pattern(100));
        File.Delete(link.TempPath);
        await using var f = await Started(links: env.Links);
        var before = ToolEvents(f.Hub);
        using (var r = await Send(f.Port, "/files/" + link.Token))
        {
            await AssertEmpty404(r);
        }
        using (var r = await Send(f.Port, "/files/" + link.Token))
        {
            await AssertEmpty404(r);
        }
        Assert.Equal(before, ToolEvents(f.Hub));
    }

    [Fact]
    public async Task Activity_events()
    {
        using var env = new LinkEnv();
        const string name = "secret-activity-name.pdf";
        var link = env.Add(Pattern(70_000), name);
        await using var f = await Started(links: env.Links);
        using (var r = await Send(f.Port, "/files/" + link.Token))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            await r.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        Assert.True(await WaitFor(() => ToolEvents(f.Hub) == 1));
        var ev = f.Hub.GetEvents().Single(e => e.Kind == ActivityKind.Tool);
        Assert.Equal("attachment download", ev.Summary);
        Assert.NotNull(ev.Duration);
        Assert.DoesNotContain(link.Token, ev.Summary);
        Assert.DoesNotContain(name, ev.Summary);

        var count = f.Hub.GetEvents().Count;
        using (await Send(f.Port, "/files/" + AttachmentLinks.NewToken())) { }
        using (await Send(f.Port, "/files/" + link.Token, auth: null)) { }
        using (await Send(f.Port, "/files/" + link.Token, "POST")) { }
        Assert.Equal(count, f.Hub.GetEvents().Count);
        Assert.True(await WaitFor(() => f.Hub.State == ActivityState.Idle));
    }

    [Fact]
    public async Task Concurrent_gets_one_wins()
    {
        using var env = new LinkEnv();
        var bytes = Pattern(300_000);
        var link = env.Add(bytes);
        await using var f = await Started(links: env.Links);
        var tasks = new[]
        {
            Send(f.Port, "/files/" + link.Token),
            Send(f.Port, "/files/" + link.Token),
        };
        var rs = await Task.WhenAll(tasks);
        try
        {
            Assert.Single(rs, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(rs, r => r.StatusCode == HttpStatusCode.NotFound);
            var ok = rs.Single(r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(bytes, await ok.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            foreach (var r in rs) r.Dispose();
        }
    }

    [Fact]
    public async Task Null_store_404()
    {
        await using var f = await Started();
        using (var r = await Send(f.Port, "/files/" + AttachmentLinks.NewToken()))
        {
            await AssertEmpty404(r);
        }
        using var init = await PostInit(f.Port, Bearer);
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
    }

    [Fact]
    public async Task Aborted_download_deletes_file()
    {
        using var env = new LinkEnv();
        var link = env.Add(new byte[32 * 1024 * 1024]);
        await using var f = await Started(links: env.Links);
        var http = new HttpClient();
        var r = await Send(f.Port, "/files/" + link.Token, option: HttpCompletionOption.ResponseHeadersRead, client: http);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        await using (var s = await r.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken))
        {
            var buf = new byte[1024];
            var n = 0;
            while (n < buf.Length)
            {
                var got = await s.ReadAsync(buf.AsMemory(n), TestContext.Current.CancellationToken);
                if (got == 0) break;
                n += got;
            }
        }
        r.Dispose();
        http.Dispose();
        Assert.True(await WaitFor(() => !File.Exists(link.TempPath)));
        Assert.True(await WaitFor(() => ToolEvents(f.Hub) >= 1));
        var last = f.Hub.GetEvents().Last(e => e.Kind == ActivityKind.Tool);
        Assert.Contains(last.Summary, new[] { "attachment download", "attachment download failed" });
    }

    [Fact]
    public async Task No_token_in_logs()
    {
        using var env = new LinkEnv();
        var capture = new CaptureProvider();
        var link = env.Add(Pattern(40_000), "secret-name-7731.pdf");
        await using var f = await Started(links: env.Links, logging: b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(capture);
        });
        var path = "/files/" + link.Token;
        using (await Send(f.Port, path, auth: null)) { }
        using (await Send(f.Port, "/files/" + AttachmentLinks.NewToken())) { }
        using (await Send(f.Port, path, "POST")) { }
        using (var ok = await Send(f.Port, path))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            await ok.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        }
        using (await Send(f.Port, path)) { }

        Assert.True(await WaitFor(() => capture.Entries.Any(e => e.Text.StartsWith("Attachment download completed (", StringComparison.Ordinal))));
        var entries = capture.Entries;
        foreach (var e in entries)
        {
            Assert.DoesNotContain(link.Token, e.Text);
            Assert.DoesNotContain("test-token-1", e.Text);
            Assert.DoesNotContain("secret-name-7731", e.Text);
            Assert.DoesNotContain(link.TempPath, e.Text);
            if (e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            {
                Assert.True(e.Level >= LogLevel.Warning, e.Category + " " + e.Level);
            }
        }
        Assert.Contains(entries, e => e.Category == "ClippysRevenge.Core.FilesEndpoint"
            && e.Level == LogLevel.Debug
            && e.Text.StartsWith("Attachment download completed (40000 bytes).", StringComparison.Ordinal));
    }
}
