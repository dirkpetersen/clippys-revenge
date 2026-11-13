using ClippysRevenge.App.Tray;
using ClippysRevenge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.App;

/// <summary>Placeholder until M8.3 lands: never confirms, so nothing is ever sent.</summary>
internal sealed class UnavailableInviteConfirmer : IInviteConfirmer
{
    public Task<InviteConfirmation> ConfirmAsync(InvitePreview preview, TimeSpan timeout, CancellationToken ct) =>
        throw new ModelContextProtocol.McpException(
            "The invite confirmation dialog is not available in this build. Ask the user to set Meeting invite mode to Draft only.");
}

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
        var worker = new OutlookWorker(hub, loggerFactory.CreateLogger("OutlookWorker"));
        worker.Start();
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClippysRevenge");
        var store = new SettingsStore(dataDir, new DpapiProtector());
        var loaded = store.Load();
        foreach (var w in loaded.Warnings)
        {
            logger.LogWarning("{Warning}", w);
        }
        var settings = loaded.Settings;
        try
        {
            StartupRegistration.Apply(settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Could not update the Run key.");
        }
        var tokens = new TokenHolder(loaded.Token);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => logger.LogCritical(e.Exception, "Unhandled UI thread exception.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.LogCritical(e.Exception, "Unobserved task exception.");

        var balloons = new BalloonCenter();
        var tray = new TrayApp(new TrayServices(
            hub,
            balloons,
            tokens,
            () => settings,
            () => TrayStats.Unknown, // TODO(M6.1): real stats
            enabled =>
            {
                settings.StartWithWindows = enabled;
                store.Save(settings);
                StartupRegistration.Apply(enabled);
            },
            () =>
            {
                var fresh = AuthToken.Generate();
                store.SaveToken(fresh);
                return fresh;
            },
            dataDir,
            // Logs live under %LOCALAPPDATA% (SPEC 5.1). "Open data folder" opens %APPDATA% on purpose: settings.json and token.bin.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippysRevenge", "logs"),
            () => worker.State == OutlookConnectionState.Unavailable,
            () =>
            {
                var ok = OutlookLauncher.TryStart();
                logger.LogInformation("Start Outlook: {Result}", ok ? "started" : "not found");
                if (ok)
                {
                    worker.Nudge();
                }
                return ok;
            }));

        instance.StartListening(() =>
        {
            logger.LogInformation("Second launch asked to open Settings.");
            tray.ShowSettings();
        });

        var inviteStores = new OutlookStores(() => settings);
        // TODO(M8.3): replace with the InviteConfirmForm-backed confirmer. Until then Send mode fails closed.
        var invites = new MeetingInvites(new OutlookMeetingCalendar(worker, inviteStores, () => settings),
            new UnavailableInviteConfirmer(), () => settings, hub, balloons, TimeZoneInfo.Local);

        var host = new McpHttpHost(new McpHostOptions
        {
            Tokens = tokens,
            Hub = hub,
            ConfigureMcp = b => b.WithTools<McpTools>(),
            ConfigureServices = s => s.AddSingleton(settings).AddSingleton(balloons).AddSingleton(worker).AddSingleton(inviteStores).AddSingleton(invites)
                .AddSingleton<IFreeBusySource>(new OutlookFreeBusySource(worker))
                .AddSingleton(sp => new FreeTimeFinder(sp.GetRequiredService<IFreeBusySource>(), TimeProvider.System, TimeZoneInfo.Local)),
            ConfigureLogging = b => b.AddDebug(),
        });
        _ = StartHostAsync(host, settings.Port, logger);

        if (loaded.FirstRun)
        {
            balloons.Post(BalloonTexts.FirstRun);
        }

        Application.Run(tray);

        // Process entry point after the message loop ended: the one allowed blocking wait.
        // Task.Run avoids the WinForms SynchronizationContext.
        Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        worker.Dispose();
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
