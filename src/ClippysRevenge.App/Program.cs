using ClippysRevenge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalExisting(SingleInstance.ShowSettingsCommand);
            return 0;
        }

        var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        var logger = loggerFactory.CreateLogger("ClippysRevenge");
        var hub = new ActivityHub();
        var settings = new Settings(); // M4.2: SettingsStore.Load()
        var tokens = new TokenHolder(AuthToken.Generate()); // M4.2: persisted DPAPI token

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => logger.LogCritical(e.Exception, "Unhandled UI thread exception.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.LogCritical(e.Exception, "Unobserved task exception.");

        instance.StartListening(() => logger.LogInformation("Second launch asked to open Settings."));

        var host = new McpHttpHost(new McpHostOptions
        {
            Tokens = tokens,
            Hub = hub,
            ConfigureMcp = b => b.WithTools<McpTools>(),
            ConfigureServices = s => s.AddSingleton(settings),
            ConfigureLogging = b => b.AddDebug(),
        });
        _ = StartHostAsync(host, settings.Port, logger);

        Application.Run(new ApplicationContext()); // M4.3: new TrayApp(...)

        // Process entry point after the message loop ended: the one allowed blocking wait.
        // Task.Run avoids the WinForms SynchronizationContext.
        Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        hub.Dispose();
        loggerFactory.Dispose();
        return 0;
    }

    private static async Task StartHostAsync(McpHttpHost host, int port, ILogger logger)
    {
        try
        {
            if (await host.StartAsync(port).ConfigureAwait(false))
            {
                logger.LogInformation("MCP server listening on 127.0.0.1:{Port}.", host.Port);
            }
            else
            {
                logger.LogError("MCP server did not start.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP server start failed.");
        }
    }
}
