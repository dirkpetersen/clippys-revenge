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

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClippysRevenge");
        var store = new SettingsStore(dataDir, new DpapiProtector());
        // A locked settings.json must show a message, not crash silently (the log and handlers are not up yet).
        SettingsLoadResult loaded;
        try
        {
            loaded = store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Clippy's Revenge could not read its settings in {dataDir}.\n\n{ex.Message}\n\nClose whatever has settings.json open and start Clippy's Revenge again.",
                "Clippy's Revenge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var settings = loaded.Settings;
        var tokens = new TokenHolder(loaded.Token);
        // Load the saved API key once so log redaction knows it (SPEC 5.1). It is never used here.
        var apiKeyUnreadable = false;
        try
        {
            store.LoadApiKey();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            apiKeyUnreadable = true;
        }

        // Rolling file log (SPEC 5.1); level is live from the LogLevel setting, the token is masked.
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippysRevenge", "logs");
        var fileLog = new RollingFileLoggerProvider(new RollingFileLogOptions
        {
            Directory = logDir,
            MinLevel = () => settings.LogLevel,
            Secrets = () => [tokens.Current, store.CachedApiKey],
        });
        var loggerFactory = LoggerFactory.Create(b => b.AddDebug().AddProvider(fileLog));
        var logger = loggerFactory.CreateLogger("ClippysRevenge");
        logger.LogInformation("Clippy's Revenge starting.");
        if (apiKeyUnreadable) logger.LogWarning("anthropic-key.bin could not be read.");
        var hub = new ActivityHub();
        var worker = new OutlookWorker(hub, loggerFactory.CreateLogger("OutlookWorker"));
        worker.Start();
        foreach (var w in loaded.Warnings)
        {
            logger.LogWarning("{Warning}", w);
        }
        try
        {
            StartupRegistration.Apply(settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Could not update the Run key.");
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => logger.LogCritical(e.Exception, "Unhandled UI thread exception.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.LogCritical(e.Exception, "Unobserved task exception.");

        var balloons = new BalloonCenter();

        // Background indexing (SPEC 10). If the index can't be opened, the tray shows an Error state and indexing stays off.
        var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClippysRevenge");
        var stores = new OutlookStores(worker, () => settings);
        MailboxIndexes? indexes = null;
        OutlookMail? mail = null;
        SyncService? sync = null;
        IDisposable? indexErrorScope = null;
        var signatures = new Signatures(SignatureRegistry.Name, Signatures.DefaultDirectory());
        OutlookReplyDraftWriter? replyWriter = null;
        DraftWorker? drafts = null;
        IDraftModel? draftModel = null;
        OutlookAiMailbox? aiMailbox = null;
        IDraftModel? apiModel = null;
        try
        {
            indexes = new MailboxIndexes(localDir, settings.SharedMailboxes.Where(m => m?.Enabled == true).Select(m => m.Smtp));
            mail = new OutlookMail(stores, indexes, settings);
            var mailSource = new OutlookMailSource(worker, stores, mail);
            sync = new SyncService(mailSource, indexes, () => settings, hub, balloons,
                logger: loggerFactory.CreateLogger<SyncService>(), reconcile: mailSource);

            // AI drafting (SPEC 19). Needs the index, so there is no worker when the index failed to open.
            var cliRunner = new ClaudeCliRunner(Path.Combine(localDir, ClaudeCliRunner.WorkDirName),
                logger: loggerFactory.CreateLogger<ClaudeCliRunner>());
            cliRunner.CleanupStaleFiles();   // SPEC 19.3 start-up cleanup
            var cliModel = new ClaudeCliModel(() => settings.AiDrafting, ClaudeCliLocator.Default, cliRunner,
                logger: loggerFactory.CreateLogger<ClaudeCliModel>());
            apiModel = new AnthropicApiModel(() => settings.AiDrafting, store.LoadApiKey,
                logger: loggerFactory.CreateLogger<AnthropicApiModel>());
            draftModel = new DraftModelSwitch(() => settings.AiDrafting, cliModel, apiModel);
            replyWriter = new OutlookReplyDraftWriter(worker, mail, stores, loggerFactory.CreateLogger<OutlookReplyDraftWriter>());
            aiMailbox = new OutlookAiMailbox(worker, mail, stores, loggerFactory.CreateLogger<OutlookAiMailbox>());
            var syncRef = sync;
            var indexesRef = indexes;
            var draftModelRef = draftModel;
            var aiMailboxRef = aiMailbox;
            // Writing-style skill (SPEC 19.6): own mailbox only; shared mailboxes use the same skill (SPEC 18 Q7).
            var styleSkill = new StyleSkill(
                new StyleSkillFiles(StylePaths.Default(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataDir)),
                () => indexesRef.For(null), () => draftModelRef, () => settings.AiDrafting,
                () => StyleCorpus.SignatureTexts(kind => signatures.Default(kind)), hub,
                logger: loggerFactory.CreateLogger<StyleSkill>());
            var styleGuide = new StyleGuideSource(styleSkill, async ct =>
            {
                var id = await aiMailboxRef.GetIdentityAsync(WorkPriority.Background, ct).ConfigureAwait(false);
                return new Recipient(id.DisplayName, id.OwnSmtp);
            });
            drafts = new DraftWorker(new DraftWorkerServices(() => settings, indexes, aiMailbox, replyWriter, draftModel,
                styleGuide,
                () => signatures.Default(SignatureKind.Reply), hub, balloons,
                () => worker.State == OutlookConnectionState.Available, () => syncRef.IsPaused, TimeZoneInfo.Local,
                Logger: loggerFactory.CreateLogger<DraftWorker>()));
            var draftsRef = drafts;
            sync.CycleCompleted += _ => draftsRef.Wake();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The email index could not be opened.");
            indexErrorScope = hub.Begin(ActivityState.Error, "index can't be opened");
        }

        // After Outlook stops hanging, resume sync at once instead of waiting for the next interval (SPEC 4).
        var stateLock = new Lock();
        var previousState = OutlookConnectionState.Unknown;
        worker.StateChanged += next =>
        {
            bool resumed;
            lock (stateLock)
            {
                resumed = previousState == OutlookConnectionState.NotResponding && next == OutlookConnectionState.Available;
                previousState = next;
            }
            if (resumed) sync?.SyncNow();
        };


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
            logDir,
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
            async () =>
            {
                try
                {
                    await stores.EnsureResolvedAsync(WorkPriority.Interactive, CancellationToken.None, includeUnconfigured: true);
                }
                catch (OutlookNotRespondingException)
                {
                    // Lookups that time out are recorded; list what is known.
                }
                return await worker.RunAsync(app => stores.List(app), WorkPriority.Interactive);
            },
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
                s.AddSingleton(signatures)
                    .AddSingleton<IDraftWriter>(new OutlookDraftWriter(worker, stores, loggerFactory.CreateLogger<OutlookDraftWriter>()))
                    .AddSingleton<CreateDraftTool>();
                if (indexes is not null && mail is not null && sync is not null)
                {
                    s.AddSingleton(indexes).AddSingleton(mail).AddSingleton(sync);
                    s.AddSingleton<IReplyDraftWriter>(replyWriter!)
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
            ConfigureLogging = b => b.AddDebug().AddProvider(fileLog),
        });
        hostRef = host;
        applier = new SettingsApplier(new SettingsApplyTargets(settings, store, tokens, p => host.RestartAsync(p), () => host.Port,
            e => StartupRegistration.Apply(e), indexes,
            sync is null ? null : sync.NotifySettingsChanged, sync is null ? null : sync.SyncNow,
            drafts is null ? null : drafts.NotifySettingsChanged), logger);
        _ = StartHostAsync(host, settings.Port, logger);
        _ = sync?.StartAsync(CancellationToken.None);
        _ = drafts?.StartAsync(CancellationToken.None);

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
            if (drafts is not null)
            {
                await drafts.StopAsync(CancellationToken.None);
            }
            if (sync is not null)
            {
                await sync.StopAsync(CancellationToken.None);
            }
            await host.DisposeAsync();
        }).Wait(TimeSpan.FromSeconds(5));
        (apiModel as IDisposable)?.Dispose();
        worker.Dispose();
        indexes?.Dispose();
        indexErrorScope?.Dispose();
        hub.Dispose();
        loggerFactory.Dispose();
        fileLog.Dispose();
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
