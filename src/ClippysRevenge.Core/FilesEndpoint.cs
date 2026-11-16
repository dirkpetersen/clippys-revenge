using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

/// <summary>GET /files/{token} (SPEC 24.4). Mapped by McpHttpHost after UseBearerAuth, so only authorized requests
/// reach it. Logs and events never hold the token, the temp path or the file name.</summary>
public static class FilesEndpoint
{
    public const string Route = AttachmentLinks.FilesPath + "/{token}";
    public const string DownloadEvent = "attachment download";
    public const string DownloadFailedEvent = "attachment download failed";
    public const string AllowedMethod = "GET";
    public const int CopyBufferSize = 81_920;

    /// <summary>endpoints.Map(Route, ctx => HandleAsync(ctx, links, hub, time, logger)): the RequestDelegate
    /// overload (all methods; the handler answers 405 itself). Called once, from McpHttpHost.Build.</summary>
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints, AttachmentLinks? links,
        ActivityHub hub, TimeProvider time, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(time);
        return endpoints.Map(Route, ctx => HandleAsync(ctx, links, hub, time, logger));
    }

    /// <summary>Never throws (every failure becomes 404, 405 or an aborted connection).</summary>
    internal static async Task HandleAsync(HttpContext context, AttachmentLinks? links, ActivityHub hub,
        TimeProvider time, ILogger? logger)
    {
        var response = context.Response;
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.ContentLength = 0;
            response.Headers["Allow"] = AllowedMethod;
            return;
        }

        var token = context.Request.RouteValues["token"] as string;
        var link = links?.TryConsume(token);
        if (links is null || link is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            response.ContentLength = 0;
            return;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(link.TempPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Safe(() => logger?.LogDebug("Attachment download: the link's file is missing or unreadable ({Type}).", ex.GetType().Name));
            response.StatusCode = StatusCodes.Status404NotFound;
            response.ContentLength = 0;
            Safe(() => links.Complete(link));
            return;
        }

        var started = false;
        var start = time.GetTimestamp();
        IDisposable? scope = null;
        try
        {
            using (stream)
            {
                try
                {
                    scope = BeginScope(hub);
                    response.StatusCode = StatusCodes.Status200OK;
                    foreach (var (key, value) in FileDownloadHeaders.Build(link.FileName, stream.Length))
                    {
                        response.Headers[key] = value;
                    }
                    started = true;
                    await stream.CopyToAsync(response.Body, CopyBufferSize, context.RequestAborted).ConfigureAwait(false);
                    await response.CompleteAsync().ConfigureAwait(false);
                    var length = stream.Length;
                    Safe(() => hub.Event(ActivityKind.Tool, DownloadEvent, time.GetElapsedTime(start)));
                    Safe(() => logger?.LogDebug("Attachment download completed ({Bytes} bytes).", length));
                }
                catch (Exception ex)
                {
                    Safe(() => hub.Event(ActivityKind.Tool, DownloadFailedEvent, time.GetElapsedTime(start)));
                    Safe(() => logger?.LogDebug("Attachment download failed ({Type}).", ex.GetType().Name));
                    Fail(context, started);
                }
                finally
                {
                    Safe(() => scope?.Dispose());
                }
            }
        }
        finally
        {
            Safe(() => links.Complete(link));
        }
    }

    private static IDisposable? BeginScope(ActivityHub hub)
    {
        try
        {
            return hub.Begin(ActivityState.Serving, DownloadEvent);
        }
        catch
        {
            return null;
        }
    }

    private static void Fail(HttpContext context, bool started)
    {
        try
        {
            if (!started && !context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentLength = 0;
                return;
            }
            context.Abort();
        }
        catch
        {
            // The connection is already gone.
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A throwing hub or logger must not break the download or skip the cleanup.
        }
    }
}
