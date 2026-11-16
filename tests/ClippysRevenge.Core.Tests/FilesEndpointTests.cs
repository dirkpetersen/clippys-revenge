using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace ClippysRevenge.Core.Tests;

public sealed class FilesEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clippy-fe-" + Guid.NewGuid().ToString("N"));
    private readonly AttachmentLinks _links;
    private readonly ActivityHub _hub = new();
    private readonly CapturingLog _log = new();

    public FilesEndpointTests()
    {
        _links = new AttachmentLinks(new AttachmentLinksOptions
        {
            Folder = _dir,
            EnsurePrivateFolder = d => Directory.CreateDirectory(d),
        });
    }

    public void Dispose()
    {
        _links.Dispose();
        _hub.Dispose();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class LifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted;
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }

    private sealed class ThrowingStream(Exception ex) : MemoryStream
    {
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw ex;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw ex;

        public override void Write(byte[] buffer, int offset, int count) => throw ex;
    }

    private AttachmentLink NewLink(int size = 100_000, string name = "secret-name-9911.pdf")
    {
        var p = _links.PrepareTempPath();
        File.WriteAllBytes(p, new byte[size]);
        return _links.Add(p, name, size);
    }

    private static (DefaultHttpContext Ctx, LifetimeFeature Life) Context(string method, string? token, Stream body)
    {
        var ctx = new DefaultHttpContext();
        var life = new LifetimeFeature();
        ctx.Features.Set<IHttpRequestLifetimeFeature>(life);
        ctx.Request.Method = method;
        if (token is not null) ctx.Request.RouteValues["token"] = token;
        ctx.Response.Body = body;
        return (ctx, life);
    }

    private async Task BrokenAsync(Exception ex, string typeName)
    {
        var link = NewLink();
        var (ctx, life) = Context("GET", link.Token, new ThrowingStream(ex));
        await FilesEndpoint.HandleAsync(ctx, _links, _hub, TimeProvider.System, _log);

        var ev = Assert.Single(_hub.GetEvents(), e => e.Kind == ActivityKind.Tool);
        Assert.Equal("attachment download failed", ev.Summary);
        Assert.NotNull(ev.Duration);
        Assert.True(life.Aborted);
        Assert.False(File.Exists(link.TempPath));
        Assert.Contains($"Attachment download failed ({typeName}).", _log.Lines);
        foreach (var line in _log.Lines)
        {
            Assert.DoesNotContain(link.Token, line);
            Assert.DoesNotContain(link.TempPath, line);
            Assert.DoesNotContain("secret-name-9911", line);
        }
    }

    [Fact]
    public Task Broken_stream_after_start() => BrokenAsync(new IOException("boom"), nameof(IOException));

    [Fact]
    public Task Cancelled_request() => BrokenAsync(new OperationCanceledException(), nameof(OperationCanceledException));

    [Fact]
    public async Task Method_405_does_not_consume()
    {
        var link = NewLink();
        var (ctx, _) = Context("HEAD", link.Token, new MemoryStream());
        var open = _links.OpenCount;
        await FilesEndpoint.HandleAsync(ctx, _links, _hub, TimeProvider.System, _log);
        Assert.Equal(405, ctx.Response.StatusCode);
        Assert.Equal("GET", ctx.Response.Headers["Allow"].ToString());
        Assert.Equal(open, _links.OpenCount);
        Assert.NotNull(_links.TryConsume(link.Token));
    }

    [Fact]
    public async Task Null_store()
    {
        var (ctx, _) = Context("GET", AttachmentLinks.NewToken(), new MemoryStream());
        await FilesEndpoint.HandleAsync(ctx, null, _hub, TimeProvider.System, _log);
        Assert.Equal(404, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Throwing_hub()
    {
        var link = NewLink(50_000);
        var body = new MemoryStream();
        var (ctx, _) = Context("GET", link.Token, body);
        using var dead = new ActivityHub();
        dead.Dispose();
        await FilesEndpoint.HandleAsync(ctx, _links, dead, TimeProvider.System, _log);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(50_000, body.Length);
        Assert.False(File.Exists(link.TempPath));
    }

    [Fact]
    public void Map_null_args()
    {
        IEndpointRouteBuilder app = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => FilesEndpoint.Map(null!, _links, _hub, TimeProvider.System, null));
        Assert.Throws<ArgumentNullException>(() => FilesEndpoint.Map(app, _links, null!, TimeProvider.System, null));
        Assert.Throws<ArgumentNullException>(() => FilesEndpoint.Map(app, _links, _hub, null!, null));
    }
}
