using ClippysRevenge.App.Tray;
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

        // Background indexing (SPEC 10). If the index can't be opened, the tray shows an Error state and indexing stays off.
        var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippysRevenge");
        var stores = new OutlookStores(() => settings);
        MailboxIndexes? indexes = null;
        OutlookMail? mail = null;
        SyncService? sync = null;
        IDisposable? indexErrorScope = null;
        try
        {
            indexes = new MailboxIndexes(localDir, settings.SharedMailboxes.Where(m => m?.Enabled == true).Select(m => m.Smtp));
            mail = new OutlookMail(stores, indexes, settings);
            var mailSource = new OutlookMailSource(worker, stores, mail);
            sync = new SyncService(mailSource, indexes, () => settings, hub, balloons,
                logger: loggerFactory.CreateLogger<SyncService>(), reconcile: mailSource);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The email index could not be opened.");
            indexErrorScope = hub.Begin(ActivityState.Error, "index can't be opened");
        }

        SettingsApplier? applier = null;
        McpHttpHost? hostRef = null;
        var tray = new TrayApp(new TrayServices(
            hub,
            balloons,
            tokens,
            () => settings,
            sync is null ? () => TrayStats.Unknown : sync.GetStats,
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
            },
            sync is null ? null : sync.SyncNow,
            sync is null ? null : () => { if (sync.IsPaused) sync.Resume(); else sync.Pause(); },
            sync is null ? null : m => sync.RebuildAsync(m),
            () => worker.RunAsync(app => stores.List(app), WorkPriority.Interactive),
            indexes is null || sync is null ? null : new SharedMailboxToggler(settings, indexes, store.Save, sync.NotifySettingsChanged),
            tab => new SettingsForm(new SettingsFormServices(
                () => settings, tokens,
                applier ?? throw new InvalidOperationException("Settings applier is not ready."),
                () => hostRef?.Port, worker, stores, indexes,
                sync is null ? null : m => sync.RebuildAsync(m),
                () => worker.State == OutlookConnectionState.Unavailable, TimeZoneInfo.Local), tab)));

        var inviteQueue = new InviteConfirmQueue(new InviteConfirmPresenter(SynchronizationContext.Current!), hub);

        instance.StartListening(() =>
        {
            logger.LogInformation("Second launch asked to open Settings.");
            tray.ShowSettings();
        });

        var invites = new MeetingInvites(new OutlookMeetingCalendar(worker, stores, () => settings),
            new QueueInviteConfirmer(inviteQueue, () => settings, TimeZoneInfo.Local), () => settings, hub, balloons, TimeZoneInfo.Local);

        var host = new McpHttpHost(new McpHostOptions
        {
            Tokens = tokens,
            Hub = hub,
            ConfigureMcp = b => b.WithTools<McpTools>().WithTools<AttachmentTools>(),
            ConfigureServices = s =>
            {
                s.AddSingleton(settings).AddSingleton(balloons).AddSingleton(worker).AddSingleton(stores).AddSingleton(invites)
                    .AddSingleton<IFreeBusySource>(new OutlookFreeBusySource(worker))
                    .AddSingleton(sp => new FreeTimeFinder(sp.GetRequiredService<IFreeBusySource>(), TimeProvider.System, TimeZoneInfo.Local));
                s.AddSingleton(new Signatures(SignatureRegistry.Name, Signatures.DefaultDirectory()))
                    .AddSingleton<IDraftWriter>(new OutlookDraftWriter(worker, stores))
                    .AddSingleton<CreateDraftTool>();
                if (indexes is not null && mail is not null && sync is not null)
                {
                    s.AddSingleton(indexes).AddSingleton(mail).AddSingleton(sync);
                    s.AddSingleton<IReplyDraftWriter>(new OutlookReplyDraftWriter(worker, mail))
                        .AddSingleton<ReplyDraftTool>();
                    s.AddSingleton<IAttachmentSaver, OutlookAttachmentSaver>()
                        .AddSingleton(sp => new AttachmentExport(sp.GetRequiredService<IAttachmentSaver>(),
                            sp.GetRequiredService<MailboxIndexes>(), () => settings, hub));
                    // Index-backed tools take Optional<T> so they stay listed (and explain the problem) when the index failed to open.
                    s.AddSingleton(new Optional<MailboxIndexes>(indexes))
                        .AddSingleton(new Optional<OutlookMail>(mail))
                        .AddSingleton(sp => new Optional<ReplyDraftTool>(sp.GetRequiredService<ReplyDraftTool>()))
                        .AddSingleton(sp => new Optional<AttachmentExport>(sp.GetRequiredService<AttachmentExport>()));
                }
                else
                {
                    s.AddSingleton(new Optional<MailboxIndexes>(null))
                        .AddSingleton(new Optional<OutlookMail>(null))
                        .AddSingleton(new Optional<ReplyDraftTool>(null))
                        .AddSingleton(new Optional<AttachmentExport>(null));
                }
            },
            ConfigureLogging = b => b.AddDebug(),
        });
        hostRef = host;
        applier = new SettingsApplier(new SettingsApplyTargets(settings, store, tokens, p => host.RestartAsync(p), () => host.Port,
            e => StartupRegistration.Apply(e), indexes,
            sync is null ? null : sync.NotifySettingsChanged, sync is null ? null : sync.SyncNow), logger);
        _ = StartHostAsync(host, settings.Port, logger);
        _ = sync?.StartAsync(CancellationToken.None);

        if (loaded.FirstRun)
        {
            balloons.Post(BalloonTexts.FirstRun);
        }

        Application.Run(tray);
        inviteQueue.Dispose();

        // Process entry point after the message loop ended: the one allowed blocking wait.
        // Task.Run avoids the WinForms SynchronizationContext.
        Task.Run(async () =>
        {
            if (sync is not null)
            {
                await sync.StopAsync(CancellationToken.None);
            }
            await host.DisposeAsync();
        }).Wait(TimeSpan.FromSeconds(5));
        worker.Dispose();
        indexes?.Dispose();
        indexErrorScope?.Dispose();
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
