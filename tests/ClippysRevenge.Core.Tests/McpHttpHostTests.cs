using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClippysRevenge.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClippysRevenge.Core.Tests;

[McpServerToolType]
public sealed class TestTools
{
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [McpServerTool(Name = "echo_test"), Description("echo")]
    public string Echo(string text) => text;

    [McpServerTool(Name = "fail_test"), Description("fail")]
    public string Fail() => throw new McpException("boom");

    [McpServerTool(Name = "wait_test"), Description("wait")]
    public async Task<string> Wait()
    {
        await Gate.Task;
        return "done";
    }
}

[McpServerToolType]
public sealed class LongNameTools
{
    [McpServerTool(Name = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Description("long")]
    public string Long() => "x";
}

public sealed record ProbeService(string Prefix);

[McpServerToolType]
public sealed class DiProbeTools
{
    [McpServerTool(Name = "di_probe"), Description("probe")]
    public string Probe(ProbeService svc, string text) => svc.Prefix + text;
}

public class McpHttpHostTests
{
    [Fact]
    public async Task DiInjectedToolParameter_NotInSchema_AndBound()
    {
        await using var f = await Started(b => b.WithTools<DiProbeTools>(), s => s.AddSingleton(new ProbeService("p:")));
        await using var c = await Client(f.Port);
        var tool = (await c.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(t => t.Name == "di_probe");
        var props = tool.JsonSchema.GetProperty("properties");
        Assert.True(props.TryGetProperty("text", out _));
        Assert.False(props.TryGetProperty("svc", out _));
        var r = await c.CallToolAsync("di_probe", new Dictionary<string, object?> { ["text"] = "x" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("p:x", Assert.IsType<TextContentBlock>(r.Content[0]).Text);
    }

    private const string Init = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";

    private sealed class Fixture : IAsyncDisposable
    {
        public ActivityHub Hub { get; } = new();
        public TokenHolder Tokens { get; } = new("test-token-1");
        public TestTools Tools { get; } = new();
        public McpHttpHost Host { get; }

        public Fixture(Action<IMcpServerBuilder>? mcp = null, Action<IServiceCollection>? services = null)
        {
            Host = new McpHttpHost(new McpHostOptions
            {
                Tokens = Tokens,
                Hub = Hub,
                ConfigureMcp = mcp ?? (b => b.WithTools(Tools)),
                ConfigureServices = services,
            });
        }

        public int Port => Host.Port!.Value;

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Hub.Dispose();
        }
    }

    private static async Task<Fixture> Started(Action<IMcpServerBuilder>? mcp = null, Action<IServiceCollection>? services = null)
    {
        var f = new Fixture(mcp, services);
        Assert.True(await f.Host.StartAsync(0));
        return f;
    }

    private static async Task<HttpResponseMessage> PostInit(int port, string? auth, string? origin = null, string path = "/mcp")
    {
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
        {
            Content = new StringContent(Init, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (auth is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", auth);
        }
        if (origin is not null)
        {
            req.Headers.TryAddWithoutValidation("Origin", origin);
        }
        return await http.SendAsync(req);
    }

    private static Task<McpClient> Client(int port, string token = "test-token-1") =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }));

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            if (cond())
            {
                return true;
            }
            await Task.Delay(20);
        }
        return cond();
    }

    [Fact]
    public async Task Start_Port0()
    {
        await using var f = await Started();
        Assert.True(f.Host.IsRunning);
        Assert.InRange(f.Port, 1, 65535);
        Assert.Equal(ActivityState.Idle, f.Hub.State);
    }

    [Fact]
    public async Task NoAuth_401_EmptyBody_NoChallenge()
    {
        await using var f = await Started();
        using var r = await PostInit(f.Port, null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(r.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task WrongToken_401()
    {
        await using var f = await Started();
        using var r = await PostInit(f.Port, "Bearer wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Origin_401()
    {
        await using var f = await Started();
        using var r = await PostInit(f.Port, "Bearer test-token-1", "http://evil.example");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task AnyPath_NoToken_401()
    {
        await using var f = await Started();
        using var http = new HttpClient();
        using var r = await http.GetAsync($"http://127.0.0.1:{f.Port}/anything", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task ValidToken_Initialize_200()
    {
        await using var f = await Started();
        using var r = await PostInit(f.Port, "Bearer test-token-1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Client_ServerInfo_And_Tools()
    {
        await using var f = await Started();
        await using var c = await Client(f.Port);
        Assert.Equal("clippy", c.ServerInfo?.Name);
        var names = (await c.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["echo_test", "fail_test", "wait_test"], names);
    }

    [Fact]
    public async Task Echo_RecordsToolEvent()
    {
        await using var f = await Started();
        await using var c = await Client(f.Port);
        var r = await c.CallToolAsync("echo_test", new Dictionary<string, object?> { ["text"] = "hi" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hi", Assert.IsType<TextContentBlock>(r.Content[0]).Text);
        Assert.True(await WaitFor(() => f.Hub.GetEvents().Count == 1));
        Assert.Equal(ActivityState.Idle, f.Hub.State);
        var ev = f.Hub.GetEvents()[^1];
        Assert.Equal(ActivityKind.Tool, ev.Kind);
        Assert.Equal("echo_test", ev.Summary);
        Assert.NotNull(ev.Duration);
        Assert.True(ev.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Wait_ServingThenIdle()
    {
        await using var f = await Started();
        await using var c = await Client(f.Port);
        var call = c.CallToolAsync("wait_test", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        Assert.True(await WaitFor(() => f.Hub.State == ActivityState.Serving));
        Assert.Equal("wait_test", f.Hub.Progress.Label);
        f.Tools.Gate.SetResult();
        await call;
        Assert.True(await WaitFor(() => f.Hub.State == ActivityState.Idle));
        Assert.Equal("wait_test", f.Hub.GetEvents()[^1].Summary);
    }

    [Fact]
    public async Task Fail_RecordsFailed()
    {
        await using var f = await Started();
        await using var c = await Client(f.Port);
        try
        {
            var r = await c.CallToolAsync("fail_test", cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(r.IsError);
        }
        catch (McpException)
        {
        }
        Assert.True(await WaitFor(() => f.Hub.GetEvents().Count == 1));
        Assert.Equal("fail_test failed", f.Hub.GetEvents()[^1].Summary);
        Assert.Equal(ActivityState.Idle, f.Hub.State);
    }

    [Fact]
    public async Task ConcurrentWaits_RefCounted()
    {
        await using var f = await Started();
        await using var c1 = await Client(f.Port);
        await using var c2 = await Client(f.Port);
        var t1 = c1.CallToolAsync("wait_test", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        var t2 = c2.CallToolAsync("wait_test", cancellationToken: TestContext.Current.CancellationToken).AsTask();
        // Both calls share one gate; use two scopes by waiting until both started.
        Assert.True(await WaitFor(() => f.Hub.State == ActivityState.Serving));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(ActivityState.Serving, f.Hub.State);
        f.Tools.Gate.SetResult();
        await Task.WhenAll(t1, t2);
        Assert.True(await WaitFor(() => f.Hub.State == ActivityState.Idle));
    }

    [Fact]
    public async Task TokenSwap()
    {
        await using var f = await Started();
        f.Tokens.Set("test-token-2");
        using var old = await PostInit(f.Port, "Bearer test-token-1");
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var fresh = await PostInit(f.Port, "Bearer test-token-2");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task PortInUse_ThenRecovers()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var busy = ((IPEndPoint)l.LocalEndpoint).Port;
            await using var f = new Fixture();
            if (busy < 1024)
            {
                return;
            }
            Assert.False(await f.Host.StartAsync(busy, TestContext.Current.CancellationToken));
            Assert.False(f.Host.IsRunning);
            Assert.Null(f.Host.Port);
            Assert.Equal(ActivityState.Error, f.Hub.State);
            Assert.Equal($"port {busy} is in use", f.Hub.Progress.Label);
            Assert.True(await f.Host.StartAsync(0, TestContext.Current.CancellationToken));
            Assert.Equal(ActivityState.Idle, f.Hub.State);
        }
        finally
        {
            l.Stop();
        }
    }

    [Fact]
    public async Task Restart_MovesPort()
    {
        await using var f = await Started();
        var a = f.Port;
        Assert.True(await f.Host.RestartAsync(0, TestContext.Current.CancellationToken));
        var b = f.Port;
        using var ok = await PostInit(b, "Bearer test-token-1");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        if (a != b)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => PostInit(a, "Bearer test-token-1"));
        }
    }

    [Fact]
    public async Task Start_WhileRunning_Throws()
    {
        await using var f = await Started();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host.StartAsync(0, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(-1)]
    public async Task Start_BadPort_Throws(int port)
    {
        await using var f = new Fixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Host.StartAsync(port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stop_Dispose_Idempotent()
    {
        var f = await Started();
        await f.Host.StopAsync(TestContext.Current.CancellationToken);
        await f.Host.StopAsync(TestContext.Current.CancellationToken);
        await f.Host.DisposeAsync();
        await f.Host.DisposeAsync();
        Assert.False(f.Host.IsRunning);
        f.Hub.Event(ActivityKind.Sync, "still works");
        f.Hub.Dispose();
    }

    [Fact]
    public async Task Start_AfterDispose_Throws()
    {
        var f = new Fixture();
        await f.Host.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Host.StartAsync(0, TestContext.Current.CancellationToken));
        f.Hub.Dispose();
    }

    [Fact]
    public void ValidateBindings_Ok() =>
        Assert.Equal(8765, McpHttpHost.ValidateBindings(["http://127.0.0.1:8765"]));

    [Theory]
    [InlineData]
    [InlineData("http://0.0.0.0:8765")]
    [InlineData("http://[::1]:8765")]
    [InlineData("http://localhost:8765")]
    [InlineData("https://127.0.0.1:8765")]
    [InlineData("http://127.0.0.1:8765", "http://127.0.0.1:8766")]
    public void ValidateBindings_Rejects(params string[] addresses)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => McpHttpHost.ValidateBindings(addresses));
        Assert.StartsWith("Refusing to serve:", ex.Message);
    }

    [Fact]
    public async Task Rejections_NeverEchoToken()
    {
        await using var f = await Started();
        foreach (var r in new[]
        {
            await PostInit(f.Port, null),
            await PostInit(f.Port, "Bearer wrong"),
            await PostInit(f.Port, "Bearer test-token-1", "http://evil.example"),
        })
        {
            using (r)
            {
                Assert.DoesNotContain("test-token-1", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.DoesNotContain("test-token-1", r.Headers.ToString());
            }
        }
    }

    [Fact]
    public async Task LongToolName_LabelCut()
    {
        await using var f = await Started(b => b.WithTools<LongNameTools>());
        await using var c = await Client(f.Port);
        var name = (await c.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))[0].Name;
        Assert.True(name.Length > McpHttpHost.MaxToolLabelLength);
        await c.CallToolAsync(name, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(await WaitFor(() => f.Hub.GetEvents().Count == 1));
        Assert.Equal(McpHttpHost.MaxToolLabelLength, f.Hub.GetEvents()[^1].Summary.Length);
    }
}
