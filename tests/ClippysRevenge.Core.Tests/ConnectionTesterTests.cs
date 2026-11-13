using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests;

public sealed class ConnectionTesterTests
{
    private const string Token = "test-token-1";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return f(request, ct);
        }
    }

    private static HttpResponseMessage Resp(HttpStatusCode code, string body = "", string media = "application/json") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, media) };

    private static string Info(string name) => "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"serverInfo\":{\"name\":\"" + name + "\"}}}";

    private static FakeHandler Handler(Func<bool, HttpResponseMessage> byAuth) =>
        new((r, _) => Task.FromResult(byAuth(r.Headers.Authorization is not null)));

    private static async Task<(McpHttpHost host, ActivityHub hub)> StartHost()
    {
        var hub = new ActivityHub();
        var host = new McpHttpHost(new McpHostOptions { Tokens = new TokenHolder(Token), Hub = hub, ConfigureMcp = b => b.WithTools<TestTools>() });
        Assert.True(await host.StartAsync(0));
        return (host, hub);
    }

    [Fact]
    public async Task RealHost_Ok()
    {
        var (host, hub) = await StartHost();
        await using var _ = host;
        using var __ = hub;
        using var t = new ConnectionTester();
        var r = await t.TestAsync(host.Port, Token, TestContext.Current.CancellationToken);
        Assert.True(r.Ok, r.Message);
        Assert.Equal($"✓ Connection works: 127.0.0.1:{host.Port} needs the token and answers MCP.", r.Message);
    }

    [Fact]
    public async Task RealHost_WrongToken()
    {
        var (host, hub) = await StartHost();
        await using var _ = host;
        using var __ = hub;
        using var t = new ConnectionTester();
        var r = await t.TestAsync(host.Port, "other-token", TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal("✗ The server rejected the token (HTTP 401).", r.Message);
    }

    [Fact]
    public async Task NotRunning()
    {
        using var t = new ConnectionTester();
        var r = await t.TestAsync(null, Token, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal("✗ The server isn't running. Check the tray icon for the reason.", r.Message);
    }

    [Fact]
    public async Task NothingListening()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var t = new ConnectionTester();
        var r = await t.TestAsync(port, Token, TestContext.Current.CancellationToken);
        Assert.False(r.Ok);
        Assert.Equal($"✗ Nothing answers on 127.0.0.1:{port}.", r.Message);
    }

    [Fact]
    public async Task NoTokenAccepted()
    {
        using var t = new ConnectionTester(Handler(_ => Resp(HttpStatusCode.OK, Info("clippy"))));
        var r = await t.TestAsync(5000, Token, TestContext.Current.CancellationToken);
        Assert.Equal("✗ The server answered without a token (HTTP 200). Another program may be using port 5000.", r.Message);
    }

    [Fact]
    public async Task Timeout()
    {
        var h = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(3000, ct);
            return Resp(HttpStatusCode.Unauthorized);
        });
        using var t = new ConnectionTester(h, TimeSpan.FromSeconds(1));
        var r = await t.TestAsync(5000, Token, TestContext.Current.CancellationToken);
        Assert.Equal("✗ The server on 127.0.0.1:5000 didn't answer within 1 s.", r.Message);
    }

    [Fact]
    public async Task OtherServer()
    {
        const string body = """{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"evil\u0007server"}}}""";
        using var t = new ConnectionTester(Handler(a => a ? Resp(HttpStatusCode.OK, body) : Resp(HttpStatusCode.Unauthorized)));
        var r = await t.TestAsync(5000, Token, TestContext.Current.CancellationToken);
        Assert.Equal("✗ Another MCP server (evilserver) is answering on port 5000.", r.Message);
    }

    [Fact]
    public async Task NotMcp()
    {
        using var t = new ConnectionTester(Handler(a => a ? Resp(HttpStatusCode.OK, "<html>", "text/html") : Resp(HttpStatusCode.Unauthorized)));
        var r = await t.TestAsync(5000, Token, TestContext.Current.CancellationToken);
        Assert.Equal("✗ The server answered, but not with an MCP initialize result.", r.Message);
    }

    [Fact]
    public async Task Step2_OtherStatus()
    {
        using var t = new ConnectionTester(Handler(a => a ? Resp(HttpStatusCode.InternalServerError) : Resp(HttpStatusCode.Unauthorized)));
        var r = await t.TestAsync(5000, Token, TestContext.Current.CancellationToken);
        Assert.Equal("✗ The server answered HTTP 500 to initialize.", r.Message);
    }

    [Fact]
    public async Task Requests_Shape()
    {
        var h = Handler(a => a ? Resp(HttpStatusCode.OK, Info("clippy")) : Resp(HttpStatusCode.Unauthorized));
        using var t = new ConnectionTester(h);
        await t.TestAsync(5123, Token, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.Requests.Count);
        Assert.All(h.Requests, r =>
        {
            Assert.Equal("http://127.0.0.1:5123/mcp", r.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.False(r.Headers.Contains("Origin"));
            var accept = r.Headers.Accept.ToString();
            Assert.Contains("application/json", accept);
            Assert.Contains("text/event-stream", accept);
        });
        Assert.Null(h.Requests[0].Headers.Authorization);
        Assert.Equal("Bearer " + Token, h.Requests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Message_NeverContainsToken()
    {
        var handlers = new HttpMessageHandler[]
        {
            Handler(_ => Resp(HttpStatusCode.OK, Info("other"))),
            Handler(a => a ? Resp(HttpStatusCode.Unauthorized, Token) : Resp(HttpStatusCode.Unauthorized, Token)),
            Handler(a => a ? Resp(HttpStatusCode.OK, Info("other")) : Resp(HttpStatusCode.Unauthorized)),
            Handler(a => a ? Resp(HttpStatusCode.OK, Token, "text/html") : Resp(HttpStatusCode.Unauthorized)),
        };
        foreach (var h in handlers)
        {
            using var t = new ConnectionTester(h);
            var r = await t.TestAsync(5000, Token, TestContext.Current.CancellationToken);
            Assert.False(r.Ok);
            Assert.DoesNotContain(Token, r.Message);
        }
        using var nr = new ConnectionTester();
        Assert.DoesNotContain(Token, (await nr.TestAsync(null, Token, TestContext.Current.CancellationToken)).Message);
    }

    [Theory]
    [InlineData("application/json", """{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"clippy"}}}""", "clippy")]
    [InlineData("text/event-stream", "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"serverInfo\":{\"name\":\"clippy\"}}}\n\n", "clippy")]
    [InlineData("text/event-stream", "data:{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"serverInfo\":{\"name\":\"clippy\"}}}\n\n", "clippy")]
    [InlineData("application/json", """{"jsonrpc":"2.0","id":1,"result":{}}""", null)]
    [InlineData("application/json", """{"result":{"serverInfo":{"name":5}}}""", null)]
    [InlineData("application/json", "", null)]
    [InlineData("application/json", "not json", null)]
    public void ReadServerName(string media, string body, string? expected) =>
        Assert.Equal(expected, ConnectionTester.ReadServerName(media, body));

    [Fact]
    public async Task ArgumentChecks()
    {
        using var t = new ConnectionTester();
        await Assert.ThrowsAsync<ArgumentException>(() => t.TestAsync(5000, "", TestContext.Current.CancellationToken));
    }
}
