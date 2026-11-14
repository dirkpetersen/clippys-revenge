using System.Reflection;
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
        var startedUtc = DateTimeOffset.UtcNow;
        SqliteHeapLimit.Ensure();   // before any index opens (process-wide 1 GiB SQLite heap cap)
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

        // Auto-update (SPEC 20). Decide before anything else starts; an update staged earlier installs right here.
        var updatePaths = UpdatePaths.Default(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var running = AppVersionInfo.FromAssembly(Assembly.GetEntryAssembly());
        var installed = updatePaths.IsInstalledCopy(Environment.ProcessPath);
        var updateStore = new UpdateStateStore(updatePaths.StateFile, loggerFactory.CreateLogger<UpdateStateStore>());
        logger.LogInformation("Version {Version} ({Commit}), installed copy: {Installed}.", running.Version, running.ShortCommit ?? "-", installed);
        var startupPlan = StartupUpdatePlan.Nothing;
        if (installed)
        {
            try
            {
                startupPlan = UpdateStartup.Prepare(updatePaths, updateStore, running.Version, settings.Updates, loggerFactory.CreateLogger("Update"));
            }
            catch (Exception ex)
            {
                logger.LogError("Update start-up check failed ({ExceptionType}).", ex.GetType().Name);
            }
        }
        if (startupPlan.InstallNow is { } staged)
        {
            PendingInstall? pendingAtStart = null;
            try
            {
                pendingAtStart = RunBlocking(() => UpdateInstall.BeginAsync(updatePaths, updateStore, staged, running.Version,
                    log: loggerFactory.CreateLogger("Update")));
            }
            catch (UpdateInstallException ex)
            {
                logger.LogWarning("Update {Version} not installed at start-up: {Reason}", staged.Version, ex.Message);
                RecordUpdateError(updateStore, logger, ex.Message);
            }
            catch (Exception ex)
            {
                // Anything unexpected must not keep the app from starting.
                logger.LogError("Update {Version} not installed at start-up ({ExceptionType}).", staged.Version, ex.GetType().Name);
                RecordUpdateError(updateStore, logger, UpdateTexts.ReasonReplaceFailed(ex.GetType().Name));
            }
            if (pendingAtStart is not null)
            {
                logger.LogInformation("Installing update {Version} at start-up.", staged.Version);
                instance.Dispose();
                var pending = pendingAtStart;
                var startOutcome = RunBlocking(() => UpdateHandover.RunAsync(updatePaths, updateStore, pending,
                    p => UpdateRestarter.Start(updatePaths, p), log: loggerFactory.CreateLogger("Update")));
                logger.LogInformation("Update handover: {Outcome}.", startOutcome);
                loggerFactory.Dispose();
                fileLog.Dispose();
                return 0;
            }
        }

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
        ClaudeCliModel? cliModel = null;
        StyleSkill? styleSkill = null;
        try
        {
            EmailIndex.Logger = loggerFactory.CreateLogger<EmailIndex>();
            indexes = new MailboxIndexes(localDir, settings.SharedMailboxes.Where(m => m?.Enabled == true).Select(m => m.Smtp));
            mail = new OutlookMail(stores, indexes, settings);
            var mailSource = new OutlookMailSource(worker, stores, mail);
            sync = new SyncService(mailSource, indexes, () => settings, hub, balloons,
                logger: loggerFactory.CreateLogger<SyncService>(), reconcile: mailSource);

            replyWriter = new OutlookReplyDraftWriter(worker, mail, stores, loggerFactory.CreateLogger<OutlookReplyDraftWriter>());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The email index could not be opened.");
            indexErrorScope = hub.Begin(ActivityState.Error, "index can't be opened");
        }

        var freeBusy = new OutlookFreeBusySource(worker);

        // AI drafting (SPEC 19). Needs the index, so there is no worker when the index failed to open.
        // A failure here is not an index failure: AI stays off and the rest of the app runs.
        if (indexes is not null && mail is not null && sync is not null && replyWriter is not null)
        {
            try
            {
            var cliRunner = new ClaudeCliRunner(Path.Combine(localDir, ClaudeCliRunner.WorkDirName),
                logger: loggerFactory.CreateLogger<ClaudeCliRunner>());
            cliRunner.CleanupStaleFiles();   // SPEC 19.3 start-up cleanup
            cliModel = new ClaudeCliModel(() => settings.AiDrafting, ClaudeCliLocator.Default, cliRunner,
                logger: loggerFactory.CreateLogger<ClaudeCliModel>());
            apiModel = new AnthropicApiModel(() => settings.AiDrafting, store.LoadApiKey,
                logger: loggerFactory.CreateLogger<AnthropicApiModel>());
            draftModel = new DraftModelSwitch(() => settings.AiDrafting, cliModel, apiModel);
            aiMailbox = new OutlookAiMailbox(worker, mail, stores, loggerFactory.CreateLogger<OutlookAiMailbox>());
            var syncRef = sync;
            var indexesRef = indexes;
            var draftModelRef = draftModel;
            var aiMailboxRef = aiMailbox;
            // Writing-style skill (SPEC 19.6): own mailbox only; shared mailboxes use the same skill (SPEC 18 Q7).
            styleSkill = new StyleSkill(
                new StyleSkillFiles(StylePaths.Default(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dataDir)),
                () => indexesRef.For(null), () => draftModelRef, () => settings.AiDrafting,
                () => StyleCorpus.SignatureTexts(kind => signatures.Default(kind)), hub,
                logger: loggerFactory.CreateLogger<StyleSkill>());
            var styleGuide = new StyleGuideSource(styleSkill,
                ct => AiOwner.ResolveAsync(aiMailboxRef, indexesRef, WorkPriority.Background, ct));
            drafts = new DraftWorker(new DraftWorkerServices(() => settings, indexes, aiMailbox, replyWriter, draftModel,
                styleGuide,
                () => signatures.Default(SignatureKind.Reply), hub, balloons,
                () => worker.State == OutlookConnectionState.Available, () => syncRef.IsPaused, TimeZoneInfo.Local,
                Logger: loggerFactory.CreateLogger<DraftWorker>(), FreeBusy: freeBusy));
            var draftsRef = drafts;
            sync.CycleCompleted += _ => draftsRef.Wake();
            }
            catch (Exception ex)
            {
                logger.LogError("AI drafting could not be set up ({ExceptionType}).", ex.GetType().Name);
                drafts = null;
                aiMailbox = null;
                draftModel = null;
                styleSkill = null;
            }
            if (styleSkill is not null)
            {
                var skillAtStart = styleSkill;
                _ = Task.Run(skillAtStart.RefreshMeetingSection);   // SPEC 21.7: off the UI thread, no consent needed, never throws
            }
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


        // AI drafts UI (SPEC 19.10). The key is saved and removed through the one SettingsStore above.
        var aiToggler = new AiDraftingToggler(settings, store.Save, () => drafts?.NotifySettingsChanged());
        AiStatusChecker? aiChecker = draftModel is null ? null
            : new AiStatusChecker(draftModel, () => cliModel?.InvalidateStatus(), s => drafts?.ReportStatus(s),
                loggerFactory.CreateLogger<AiStatusChecker>());
        AiTrayServices? aiTray = drafts is null || aiMailbox is null || aiChecker is null ? null
            : new AiTrayServices(drafts.DraftSelectedAsync, aiMailbox.ShowDraftsAsync, aiToggler, aiChecker);
        var aiMailboxForUi = aiMailbox;
        var indexesForUi = indexes;
        var aiSettings = new AiSettingsServices(aiToggler, () => store.LoadApiKey() is { } k && SettingsStore.IsValidApiKeyFormat(k), store.SaveApiKey,
            TrayApp.ShellOpen, aiChecker, styleSkill,
            aiMailboxForUi is null || indexesForUi is null ? null
                : ct => AiOwner.ResolveAsync(aiMailboxForUi, indexesForUi, WorkPriority.Interactive, ct),
            drafts is null ? null : drafts.Today);

        SettingsApplier? applier = null;
        McpHttpHost? hostRef = null;
        UpdateService? updates = null;
        TrayApp? trayRef = null;
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
                () => worker.State == OutlookConnectionState.Unavailable, TimeZoneInfo.Local, aiSettings,
                ShowAboutAndCheck: () => trayRef?.ShowAbout(checkNow: true)), tab),
            aiTray,
            CreateAboutForm: () =>
            {
                // The form owns its own icon set; the tray's icons stay the tray's.
                var icons = new TrayIcons();
                var form = new AboutForm(updates ?? throw new InvalidOperationException("Updates are not ready."),
                    TrayApp.ShellOpen, icons.Get(TrayIconKey.Idle));
                form.Disposed += (_, _) => icons.Dispose();
                return form;
            }));
        trayRef = tray;

        // Auto-update service (SPEC 20). Started after the server start was attempted, see AfterStartAsync.
        PendingInstall? handover = null;   // set by RequestInstall on a pool thread, read after the message loop
        var updateClient = new UpdateClient(running.Version);
        updates = new UpdateService(new UpdateServiceOptions(
            () => settings, updateClient, updateStore, updatePaths, running, installed,
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
            IsBusy: () => hub.IsActive(ActivityState.Serving) || hub.IsActive(ActivityState.Drafting)
                || tray.IsSettingsOpen,
            ReadExeVersion: UpdateRestarter.ReadExeVersion,
            RequestInstall: async (stagedUpdate, ct) =>
            {
                try
                {
                    var p = await UpdateInstall.BeginAsync(updatePaths, updateStore, stagedUpdate, running.Version,
                        log: loggerFactory.CreateLogger("Update"), ct: ct);
                    Volatile.Write(ref handover, p);
                    tray.ExitForUpdate();
                    return null;
                }
                catch (UpdateInstallException ex)
                {
                    return ex.Message;
                }
            },
            hub, balloons, startedUtc, Logger: loggerFactory.CreateLogger<UpdateService>(),
            // Update now is the user's own action; an open Settings window (About starts from there) must not block it.
            IsBusyManual: () => hub.IsActive(ActivityState.Serving) || hub.IsActive(ActivityState.Drafting)));

        instance.StartListening(() =>
        {
            logger.LogInformation("Second launch asked to open Settings.");
            tray.ShowSettings();
        });

        var meetingNotes = replyWriter is null ? null
            : new MeetingNotes(replyWriter, replyWriter, () => signatures.Default(SignatureKind.Reply),
                loggerFactory.CreateLogger<MeetingNotes>());
        var draftInvites = new DraftInvites(
            new OutlookDraftInviteCalendar(worker, loggerFactory.CreateLogger<OutlookDraftInviteCalendar>()),
            new DraftInvitesStore(Path.Combine(dataDir, DraftInvitesStore.FileName), loggerFactory.CreateLogger<DraftInvitesStore>()),
            () => worker.State == OutlookConnectionState.Available,
            TimeZoneInfo.Local, logger: loggerFactory.CreateLogger<DraftInvites>());
        if (sync is not null) sync.CycleCompleted += _ => draftInvites.OnSyncCycleCompleted();
        var invites = new MeetingInvites(
            new OutlookMeetingCalendar(worker, stores, () => settings, loggerFactory.CreateLogger<OutlookMeetingCalendar>()),
            () => settings, hub, TimeZoneInfo.Local, tracker: draftInvites, notes: meetingNotes);

        var host = new McpHttpHost(new McpHostOptions
        {
            Tokens = tokens,
            Hub = hub,
            ConfigureMcp = b => b.WithTools<McpTools>().WithTools<AttachmentTools>(),
            ConfigureServices = s =>
            {
                s.AddSingleton(settings).AddSingleton(balloons).AddSingleton(worker).AddSingleton(stores).AddSingleton(invites)
                    .AddSingleton<IFreeBusySource>(freeBusy)
                    .AddSingleton(sp => new FreeTimeFinder(sp.GetRequiredService<IFreeBusySource>(), TimeProvider.System, TimeZoneInfo.Local, logger: loggerFactory.CreateLogger<FreeTimeFinder>()));
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
        var hostStart = StartHostAsync(host, settings.Port, logger,
            startupPlan.ConfirmAfterStart ? NewInstanceBindRetry : TimeSpan.Zero);
        _ = AfterStartAsync(hostStart, SynchronizationContext.Current!, startupPlan, updatePaths, updateStore, running, updates, hub, balloons, loggerFactory);
        _ = sync?.StartAsync(CancellationToken.None);
        _ = drafts?.StartAsync(CancellationToken.None);

        if (loaded.FirstRun)
        {
            balloons.Post(BalloonTexts.FirstRun);
        }

        Application.Run(tray);

        // Process entry point after the message loop ended: the one allowed blocking wait.
        // Task.Run avoids the WinForms SynchronizationContext.
        // The update service first: it waits for a file swap that is still running, so a handover is known below.
        WaitBounded(() => updates!.StopAsync(CancellationToken.None), UpdateService.InstallStopWait + TimeSpan.FromSeconds(5), logger, "update service");
        var pendingHandover = Volatile.Read(ref handover);
        // Then the MCP host, before anything else: the new version needs its port, and no new requests are wanted.
        var hostStopped = WaitBounded(() => host.DisposeAsync().AsTask(), HostStopWait, logger, "MCP host");
        WaitBounded(async () =>
        {
            if (drafts is not null)
            {
                await drafts.StopAsync(CancellationToken.None);
            }
            if (sync is not null)
            {
                await sync.StopAsync(CancellationToken.None);
            }
        }, TimeSpan.FromSeconds(5), logger, "background services");
        (apiModel as IDisposable)?.Dispose();
        worker.Dispose();
        indexes?.Dispose();
        indexErrorScope?.Dispose();
        hub.Dispose();
        updateClient.Dispose();
        if (pendingHandover is not null && !hostStopped)
        {
            // The port may still be held, so the new version could not bind it. The swapped exe starts at the next
            // launch and confirms itself then (SPEC 20.9).
            logger.LogWarning("Update handover skipped: the MCP server did not stop in time.");
        }
        else if (pendingHandover is not null)
        {
            instance.Dispose();   // releases the mutex and the pipe before the new exe starts (SPEC 14)
            var outcome = RunBlocking(() => UpdateHandover.RunAsync(updatePaths, updateStore, pendingHandover,
                p => UpdateRestarter.Start(updatePaths, p), log: loggerFactory.CreateLogger("Update")));
            logger.LogInformation("Update handover: {Outcome}.", outcome);
        }
        loggerFactory.Dispose();
        fileLog.Dispose();
        return 0;
    }

    /// <summary>Process entry point only: blocks the STA main thread before the message loop starts or after it ended.
    /// Task.Run avoids the WinForms SynchronizationContext.</summary>
    private static T RunBlocking<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private static readonly TimeSpan HostStopWait = TimeSpan.FromSeconds(10);
    /// <summary>The first start of a freshly updated process: the old process may still hold the MCP port for a moment.</summary>
    private static readonly TimeSpan NewInstanceBindRetry = TimeSpan.FromSeconds(10);

    /// <summary>Process entry point only, after the message loop ended. False when it didn't finish in time or failed.</summary>
    private static bool WaitBounded(Func<Task> work, TimeSpan limit, ILogger logger, string what)
    {
        try
        {
            if (Task.Run(work).Wait(limit))
            {
                return true;
            }
            logger.LogWarning("Stopping the {What} took longer than {Seconds} s.", what, (int)limit.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Stopping the {What} failed ({ExceptionType}).", what, ex.GetBaseException().GetType().Name);
        }
        return false;
    }

    private static void RecordUpdateError(UpdateStateStore store, ILogger logger, string reason)
    {
        try
        {
            store.Update(s => s.LastError = reason);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The update state could not be saved ({ExceptionType}).", e.GetType().Name);
        }
    }

    /// <summary>Once the server start was attempted and the message loop pumps: confirm a fresh update, show notices,
    /// then start the update schedule (so the first check is 60 s after a working tray).</summary>
    private static async Task AfterStartAsync(Task<bool> hostStart, SynchronizationContext ui, StartupUpdatePlan plan,
        UpdatePaths paths, UpdateStateStore store, AppVersionInfo running, UpdateService updates, ActivityHub hub,
        BalloonCenter balloons, ILoggerFactory loggerFactory)
    {
        var hostStarted = await hostStart.ConfigureAwait(false);
        var log = loggerFactory.CreateLogger("Update");
        ui.Post(async _ =>
        {
            try
            {
                UpdateNotice? notice = plan.Notice;
                if (plan.ConfirmAfterStart && !hostStarted)
                {
                    // A new version that can't serve isn't a working update: stay unconfirmed so the old process rolls back.
                    log.LogWarning("Update not confirmed: the MCP server did not start.");
                }
                else if (plan.ConfirmAfterStart)
                {
                    var confirmed = await UpdateStartup.ConfirmAsync(store, running.Version, log: log);
                    if (confirmed is not null)
                    {
                        notice = confirmed;
                        _ = UpdateStartup.CleanupOldAsync(paths, store, running.Version, log: log);
                    }
                    else if (notice?.Kind == UpdateNoticeKind.Updated)
                    {
                        notice = null;
                    }
                }
                if (notice is not null)
                {
                    if (notice.Kind == UpdateNoticeKind.RolledBack)
                    {
                        balloons.Post(BalloonTexts.UpdateRolledBack(notice.FromVersion, notice.ToVersion));
                        hub.Event(ActivityKind.Update, UpdateTexts.EventRolledBack(notice.FromVersion, notice.ToVersion));
                    }
                    else
                    {
                        balloons.Post(BalloonTexts.Updated(notice.ToVersion));
                        hub.Event(ActivityKind.Update, UpdateTexts.EventUpdated(notice.FromVersion, notice.ToVersion));
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError("Update notice failed ({ExceptionType}).", ex.GetType().Name);
            }
            _ = updates.StartAsync(CancellationToken.None);
        }, null);
    }

    private static async Task<bool> StartHostAsync(McpHttpHost host, int port, ILogger logger, TimeSpan bindRetryFor)
    {
        try
        {
            if (await host.StartAsync(port, bindRetryFor: bindRetryFor).ConfigureAwait(false))
            {
                logger.LogInformation("MCP server listening on 127.0.0.1:{Port}.", host.Port);
                return true;
            }
            logger.LogError("MCP server did not start.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP server start failed.");
        }
        return false;
    }
}
