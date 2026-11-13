using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClippysRevenge.Core;

public sealed class McpHostOptions
{
    public required TokenHolder Tokens { get; init; }
    public required ActivityHub Hub { get; init; }
    /// <summary>Registers tools (App: b => b.WithTools&lt;McpTools&gt;()). Null = no tools.</summary>
    public Action<IMcpServerBuilder>? ConfigureMcp { get; init; }
    /// <summary>Extra app services (singleton instances that must outlive a restart). Null = none.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
    /// <summary>Called after Logging.ClearProviders(). Null = no logging.</summary>
    public Action<ILoggingBuilder>? ConfigureLogging { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed class McpHttpHost : IAsyncDisposable
{
    public const string McpPath = "/mcp";
    public const string ServerName = "clippy";
    public const int MaxToolLabelLength = 64;

    private readonly McpHostOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private IDisposable? _errorScope;
    private int _port;
    private volatile bool _running;
    private bool _disposed;

    public McpHttpHost(McpHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Tokens);
        ArgumentNullException.ThrowIfNull(options.Hub);
        _options = options;
    }

    public int? Port => _running ? Volatile.Read(ref _port) : null;

    public bool IsRunning => _running;

    public async Task<bool> StartAsync(int port, CancellationToken ct = default)
    {
        ValidatePort(port);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("The MCP host is already running.");
            }
            return await StartCoreAsync(port, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RestartAsync(int port, CancellationToken ct = default)
    {
        ValidatePort(port);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync(ct).ConfigureAwait(false);
            return await StartCoreAsync(port, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static int ValidateBindings(IReadOnlyCollection<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 1
            && Uri.TryCreate(addresses.First(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.Host == "127.0.0.1"
            && uri.Port is >= 1 and <= 65535)
        {
            return uri.Port;
        }
        throw new InvalidOperationException(
            "Refusing to serve: Clippy only listens on http://127.0.0.1:<port>. Found: " + string.Join(", ", addresses));
    }

    private static void ValidatePort(int port)
    {
        if (port != 0 && (port < Settings.MinPort || port > Settings.MaxPort))
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be 0 or between 1024 and 65535.");
        }
    }

    private async Task<bool> StartCoreAsync(int port, CancellationToken ct)
    {
        _errorScope?.Dispose();
        _errorScope = null;

        WebApplication? app = null;
        ILogger? logger = null;
        try
        {
            app = Build(port);
            logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger<McpHttpHost>();
            await app.StartAsync(ct).ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            int bound;
            try
            {
                bound = ValidateBindings(addresses.ToArray());
            }
            catch (InvalidOperationException ex)
            {
                await DisposeAppAsync(app).ConfigureAwait(false);
                app = null;
                logger?.LogError(ex, "Unexpected HTTP binding.");
                return Fail("unexpected HTTP binding");
            }
            _app = app;
            Volatile.Write(ref _port, bound);
            _running = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (app is not null)
            {
                await DisposeAppAsync(app).ConfigureAwait(false);
            }
            var label = HasAddressInUse(ex)
                ? "port " + port.ToString(CultureInfo.InvariantCulture) + " is in use"
                : "HTTP server failed to start";
            logger?.LogError(ex, "{Label}", label);
            return Fail(label);
        }
    }

    private bool Fail(string label)
    {
        _running = false;
        _errorScope = _options.Hub.Begin(ActivityState.Error, label);
        return false;
    }

    private static bool HasAddressInUse(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is AddressInUseException)
            {
                return true;
            }
            if (ex is AggregateException agg && agg.InnerExceptions.Any(HasAddressInUse))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task DisposeAppAsync(WebApplication app)
    {
        try
        {
            await app.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Half-built app; nothing more to do.
        }
        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Same.
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        var app = _app;
        _app = null;
        _running = false;
        _errorScope?.Dispose();
        _errorScope = null;
        if (app is null)
        {
            return;
        }
        try
        {
            await app.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        // No env-var or file configuration may add bindings.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        _options.ConfigureLogging?.Invoke(builder.Logging);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port);
            k.AddServerHeader = false;
        });
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton(_options.Tokens);
        builder.Services.AddSingleton(_options.Hub);
        builder.Services.AddSingleton(_options.Time);
        _options.ConfigureServices?.Invoke(builder.Services);

        var hub = _options.Hub;
        var time = _options.Time;
        var version = typeof(McpHttpHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var mcp = builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = version })
            .WithHttpTransport(t => t.Stateless = true)
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (ctx, ct) =>
            {
                var name = Label(ctx.Params?.Name);
                var start = time.GetTimestamp();
                var failed = true;
                using (hub.Begin(ActivityState.Serving, name))
                {
                    try
                    {
                        var r = await next(ctx, ct).ConfigureAwait(false);
                        failed = r.IsError == true;
                        return r;
                    }
                    finally
                    {
                        hub.Event(ActivityKind.Tool, failed ? name + " failed" : name, time.GetElapsedTime(start));
                    }
                }
            }));
        _options.ConfigureMcp?.Invoke(mcp);

        var app = builder.Build();
        app.UseBearerAuth();
        app.MapMcp(McpPath);
        return app;
    }

    private static string Label(string? name)
    {
        var s = name?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return "(unknown)";
        }
        return s.Length > MaxToolLabelLength ? s[..MaxToolLabelLength] : s;
    }
}
