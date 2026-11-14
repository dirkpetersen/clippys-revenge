using System.ComponentModel;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using ClippysRevenge.Core;
using Timer = System.Windows.Forms.Timer;

namespace ClippysRevenge.App.Tray;

internal sealed record AiTrayServices(
    Func<CancellationToken, Task<ManualDraftResult>> DraftSelected,   // drafts.DraftSelectedAsync
    Func<CancellationToken, Task<bool>> ShowDrafts,                  // aiMailbox.ShowDraftsAsync
    AiDraftingToggler Toggler,
    AiStatusChecker Checker);

internal sealed record TrayServices(
    ActivityHub Hub,
    BalloonCenter Balloons,
    TokenHolder Token,
    Func<Settings> GetSettings,
    Func<TrayStats> GetStats,
    Action<bool>? SetStartWithWindows,
    Func<string>? RegenerateToken,
    string DataFolder,
    string LogFolder,
    Func<bool> IsOutlookUnavailable,
    Func<bool>? StartOutlook,
    Action? SyncNow = null,
    Action? TogglePause = null,
    Func<string, Task>? Rebuild = null,
    Func<Task<IReadOnlyList<MailboxStore>>>? ListMounted = null,
    SharedMailboxToggler? SharedToggler = null,
    Func<SettingsTab, Form>? CreateSettingsForm = null,
    AiTrayServices? Ai = null,
    Func<AboutForm>? CreateAboutForm = null,
    AttentionCount? Attention = null);

/// <summary>Tray icon and menu (SPEC 6.1, 6.2). All UI objects are touched on the UI thread only.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly TrayServices _s;
    private readonly SynchronizationContext _ui;
    private readonly TrayIcons _icons;
    private readonly TrayPresenter _presenter = new();
    private readonly StateBalloonPolicy _policy = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _notify;
    private readonly Timer _animTimer = new() { Interval = 250 };
    private readonly Timer _houseTimer = new() { Interval = 1000 };

    private readonly ToolStripMenuItem _status1 = new() { Enabled = false };
    private readonly ToolStripMenuItem _status2 = new() { Enabled = false };
    private readonly ToolStripMenuItem _startOutlook = new("Start Outlook");
    private readonly ToolStripMenuItem _syncNow = new("Sync now") { Enabled = false };
    private readonly ToolStripMenuItem _pause = new("Pause indexing") { Enabled = false };
    private readonly ToolStripMenuItem _rebuild = new("Rebuild index…") { Enabled = false };
    private readonly ToolStripMenuItem _shared = new("Shared mailboxes");
    private readonly ToolStripMenuItem _ai = new(AiMenuTexts.Submenu);
    private readonly ToolStripMenuItem _aiAuto = new(AiMenuTexts.Auto) { CheckOnClick = false };
    private readonly ToolStripMenuItem _aiDraftSelected = new(AiMenuTexts.DraftSelected);
    private readonly ToolStripMenuItem _aiShowDrafts = new(AiMenuTexts.ShowDrafts);
    private readonly ToolStripMenuItem _aiSettings = new(AiMenuTexts.Settings);
    private readonly ToolStripMenuItem _startWithWindows = new("Start with Windows") { CheckOnClick = false };

    private TrayIconKey _shownKey = TrayIconKey.Idle;
    private TrayBadgeRenderer? _badge;
    private string _badgeText = "";    // wanted
    private string _shownBadge = "";   // on screen
    private bool _animated;
    private int _frame;
    private int _renderQueued;
    private volatile bool _disposed;
    private IReadOnlyList<MailboxStore>? _mounted;
    private int _listing;
    private ActivityForm? _activity;
    private Form? _settingsForm;
    private AboutForm? _about;
    private volatile bool _settingsOpen;
    private int _aiBusy;
    private BalloonAction _lastBalloonAction = BalloonAction.None;
    public TrayApp(TrayServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _s = services;

        // Creating a WinForms control installs the WindowsFormsSynchronizationContext on this thread.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext ctx)
        {
            _menu.Dispose();
            throw new InvalidOperationException("TrayApp must be created on the WinForms UI thread.");
        }
        _ui = ctx;

        try
        {
            _icons = new TrayIcons();
        }
        catch
        {
            _menu.Dispose();
            throw;
        }

        if (_s.Attention is not null)
        {
            _badge = new TrayBadgeRenderer(AttentionBadge.PickIconSize(SystemInformation.SmallIconSize.Width));
        }

        BuildMenu();

        _notify = new NotifyIcon
        {
            Icon = _icons.Get(TrayIconKey.Idle),
            Text = "Clippy — idle",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => ShowActivity();
        _notify.BalloonTipClicked += (_, _) =>
        {
            var action = _lastBalloonAction;
            _lastBalloonAction = BalloonAction.None;
            if (action == BalloonAction.ShowDrafts && _s.Ai is not null)
            {
                ShowDraftsInOutlook();
            }
        };
        _notify.BalloonTipClosed += (_, _) => _lastBalloonAction = BalloonAction.None;

        _animTimer.Tick += (_, _) =>
        {
            if (_animated)
            {
                _frame = (_frame + 1) % 4;
                SetIcon(TrayIconKey.Backfill0 + _frame);
            }
        };
        _houseTimer.Tick += (_, _) => Render();
        _houseTimer.Start();

        _s.Hub.Changed += OnHubChanged;
        if (_s.Attention is not null)
        {
            _s.Attention.Changed += OnHubChanged;
        }
        _s.Balloons.Shown += OnBalloon;
        Render();
    }

    /// <summary>Any thread; opens (or raises) the single Settings window on the UI thread.</summary>
    public void ShowSettings(SettingsTab tab = SettingsTab.Connection) => PostToUi(() =>
    {
        if (_s.CreateSettingsForm is null)
        {
            return;
        }
        if (_settingsForm is { IsDisposed: false } open)
        {
            if (open is SettingsForm sf)
            {
                sf.SelectTab(tab);
            }
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }
            open.Activate();
            return;
        }
        var form = _s.CreateSettingsForm(tab);
        _settingsForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, form))
            {
                _settingsForm = null;
                _settingsOpen = false;
            }
            form.Dispose();
        };
        _settingsOpen = true;
        form.Show();
    });

    /// <summary>True while the Settings window is open. Any thread.</summary>
    public bool IsSettingsOpen => _settingsOpen;

    /// <summary>Any thread; opens (or raises) the single About window; checkNow also starts a check.</summary>
    public void ShowAbout(bool checkNow = false) => PostToUi(() =>
    {
        if (_s.CreateAboutForm is null)
        {
            return;
        }
        if (_about is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }
            open.Activate();
            if (checkNow)
            {
                open.StartCheck();
            }
            return;
        }
        var form = _s.CreateAboutForm();
        _about = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_about, form))
            {
                _about = null;
            }
            form.Dispose();
        };
        form.Show();
        if (checkNow)
        {
            form.StartCheck();
        }
    });

    /// <summary>Any thread; hides the icon and ends the message loop so Program can hand over (SPEC 20.9).</summary>
    public void ExitForUpdate() => PostToUi(() =>
    {
        _notify.Visible = false;
        ExitThread();
    });

    /// <summary>Any thread; opens (or raises) the single Activity window on the UI thread.</summary>
    public void ShowActivity() => PostToUi(() =>
    {
        if (_activity is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized)
            {
                open.WindowState = FormWindowState.Normal;
            }
            open.Activate();
            return;
        }
        _activity = new ActivityForm(_s.Hub);
        _activity.FormClosed += (_, _) =>
        {
            _activity?.Dispose();
            _activity = null;
        };
        _activity.Show();
    });

    private void PostToUi(Action action) => _ui.Post(_ =>
    {
        if (!_disposed)
        {
            action();
        }
    }, null);

    // Hub.Changed can fire on the caller's thread and the hub timer thread concurrently:
    // do nothing here except queue one coalesced render on the UI thread.
    private void OnHubChanged()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
        {
            _ui.Post(_ =>
            {
                if (!_disposed)
                {
                    Render();
                }
            }, null);
        }
    }

    private void OnBalloon(Balloon b) => PostToUi(() =>
    {
        var icon = b.Kind switch
        {
            BalloonKind.Warning => ToolTipIcon.Warning,
            BalloonKind.Error => ToolTipIcon.Error,
            _ => ToolTipIcon.Info,
        };
        _lastBalloonAction = b.Action;
        _notify.ShowBalloonTip(5000, BalloonTexts.Title, b.Text, icon);
    });

    private void Render()
    {
        Interlocked.Exchange(ref _renderQueued, 0);
        if (_disposed)
        {
            return;
        }
        var state = _s.Hub.State;
        var progress = _s.Hub.Progress;
        var events = _s.Hub.GetEvents();
        ActivityEvent? lastTool = null;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i].Kind == ActivityKind.Tool)
            {
                lastTool = events[i];
                break;
            }
        }

        var count = _s.Attention?.Total ?? 0;
        var view = _presenter.Update(state, progress, _s.GetStats(), lastTool, attentionCount: count);
        _badgeText = AttentionBadge.Text(count);
        if (view.Animated)
        {
            if (!_animated)
            {
                _animated = true;
                _frame = 0;
                SetIcon(TrayIconKey.Backfill0);
                _animTimer.Start();
            }
            else
            {
                SetIcon(TrayIconKey.Backfill0 + _frame);
            }
        }
        else
        {
            _animated = false;
            _animTimer.Stop();
            _frame = 0;
            SetIcon(view.Icon);
        }
        _notify.Text = view.Tooltip;
        _status1.Text = view.StatusLine;
        _status2.Text = view.SyncLine;
        _startOutlook.Visible = _s.IsOutlookUnavailable();
        _pause.Text = view.PauseMenuText;

        var balloon = _policy.Observe(state, progress, _s.GetSettings().NotifyOnErrors);
        if (balloon is not null)
        {
            _s.Balloons.Post(balloon.Text, balloon.Kind);
        }
    }

    private void SetIcon(TrayIconKey key)
    {
        if (key == _shownKey && _badgeText == _shownBadge)
        {
            return;
        }
        Icon? icon = null;
        var shown = "";
        if (_badgeText.Length > 0 && _badge is not null)
        {
            try
            {
                icon = _badge.Get(key, _badgeText);
                shown = _badgeText;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or OutOfMemoryException)
            {
                // GDI+ failed: no badge from now on (no retry every tick), plain icons only.
                _badge.Dispose();
                _badge = null;
            }
        }
        _shownKey = key;
        _shownBadge = shown;
        _notify.Icon = icon ?? _icons.Get(key);
    }

    private void BuildMenu()
    {
        var copyCmd = new ToolStripMenuItem("Copy \"claude mcp add\" command");
        copyCmd.Click += (_, _) => CopyToClipboard(() => TrayText.McpAddCommand(_s.GetSettings().Port, _s.Token.Current), true);
        var copyToken = new ToolStripMenuItem("Copy auth token");
        copyToken.Click += (_, _) => CopyToClipboard(() => _s.Token.Current, false);
        var regen = new ToolStripMenuItem("Regenerate auth token…") { Enabled = _s.RegenerateToken is not null };
        regen.Click += (_, _) => RegenerateToken();

        var activity = new ToolStripMenuItem("Activity…");
        activity.Click += (_, _) => ShowActivity();
        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();
        var logs = new ToolStripMenuItem("Open log folder");
        logs.Click += (_, _) => OpenFolder(_s.LogFolder);
        var data = new ToolStripMenuItem("Open data folder");
        data.Click += (_, _) => OpenFolder(_s.DataFolder);

        var about = new ToolStripMenuItem("About Clippy's Revenge…") { Enabled = _s.CreateAboutForm is not null };
        about.Click += (_, _) => ShowAbout();
        _startWithWindows.Enabled = _s.SetStartWithWindows is not null;
        _startWithWindows.Click += (_, _) => _s.SetStartWithWindows?.Invoke(!_s.GetSettings().StartWithWindows);
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            _notify.Visible = false;
            ExitThread();
        };

        _startOutlook.Enabled = _s.StartOutlook is not null;
        _startOutlook.Click += (_, _) =>
        {
            if (_s.StartOutlook?.Invoke() == false)
            {
                _s.Balloons.PostUserFeedback(BalloonTexts.OutlookNotFound);
            }
        };
        _syncNow.Click += (_, _) => _s.SyncNow?.Invoke();
        _pause.Enabled = _s.TogglePause is not null;
        _pause.Click += (_, _) => _s.TogglePause?.Invoke();
        _rebuild.Enabled = _s.Rebuild is not null;
        _rebuild.Click += (_, _) => AskRebuild();
        _shared.DropDownOpening += (_, _) => RebuildShared();
        _shared.DropDownItems.Add(new ToolStripMenuItem("Manage in Settings…", null, (_, _) => ShowSettings(SettingsTab.Index)));
        BuildAiMenu();
        _menu.Opening += (_, _) =>
        {
            _startWithWindows.Checked = _s.GetSettings().StartWithWindows;
            _syncNow.Enabled = _s.SyncNow is not null && !_s.GetStats().Paused;
            ApplyAiMenu();
        };

        _menu.Items.AddRange([
            _status1, _status2, new ToolStripSeparator(),
            _startOutlook, _syncNow, _pause, _rebuild, _shared, _ai, new ToolStripSeparator(),
            copyCmd, copyToken, regen, new ToolStripSeparator(),
            activity, settings, logs, data, new ToolStripSeparator(),
            _startWithWindows, about, exit,
        ]);
    }

    private void BuildAiMenu()
    {
        _aiAuto.Click += (_, _) => ToggleAi();
        _aiDraftSelected.Click += (_, _) => DraftSelected();
        _aiShowDrafts.Click += (_, _) => ShowDraftsInOutlook();
        _aiSettings.Enabled = _s.CreateSettingsForm is not null;
        _aiSettings.Click += (_, _) => ShowSettings(SettingsTab.AiDrafts);
        _ai.DropDownOpening += (_, _) => ApplyAiMenu();
        _ai.DropDownItems.AddRange([_aiAuto, _aiDraftSelected, _aiShowDrafts, new ToolStripSeparator(), _aiSettings]);
    }

    private void ApplyAiMenu()
    {
        var m = AiDraftsMenu.Build(_s.GetSettings().AiDrafting, _s.Ai is not null, _s.IsOutlookUnavailable(),
            Volatile.Read(ref _aiBusy) == 1);
        _aiAuto.Checked = m.AutoChecked;
        _aiAuto.Enabled = m.AutoEnabled;
        _aiDraftSelected.Enabled = m.DraftSelectedEnabled;
        _aiShowDrafts.Visible = m.ShowDraftsVisible;
        _aiShowDrafts.Enabled = m.ShowDraftsEnabled;
    }

    /// <summary>Draft replies automatically: consent first, then a backend check, then Enabled (SPEC 19.10).</summary>
    private async void ToggleAi()
    {
        var ai = _s.Ai;
        if (ai is null || Interlocked.Exchange(ref _aiBusy, 1) == 1)
        {
            return;
        }
        try
        {
            var step = AiToggle.Next(_s.GetSettings().AiDrafting);
            if (step == AiToggleStep.TurnOff)
            {
                ai.Toggler.SetEnabled(false);
                return;
            }
            if (step == AiToggleStep.AskConsent)
            {
                if (!AiConsentDialog.Ask(null))
                {
                    return;
                }
                ai.Toggler.RecordConsent();
            }
            var status = await ai.Checker.CheckAsync(refresh: false, CancellationToken.None);
            if (AiToggle.AfterCheck(status) == AiToggleOutcome.OpenSettings)
            {
                ShowSettings(SettingsTab.AiDrafts);
                return;
            }
            ai.Toggler.SetEnabled(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _s.Balloons.PostUserFeedback(AiMenuTexts.ToggleFailed(ex.Message));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Type only: the message could carry paths or other text we don't want in a balloon.
            _s.Balloons.PostUserFeedback(AiMenuTexts.ToggleFailed("something went wrong (" + ex.GetType().Name + ")"));
        }
        finally
        {
            Interlocked.Exchange(ref _aiBusy, 0);
        }
    }

    /// <summary>Draft a reply to the selected email (manual path, SPEC 19.7). Two clicks queue; no busy guard.</summary>
    private async void DraftSelected()
    {
        var ai = _s.Ai;
        if (ai is null)
        {
            return;
        }
        try
        {
            if (!_s.GetSettings().AiDrafting.HasConsent)
            {
                if (!AiConsentDialog.Ask(null))
                {
                    return;
                }
                ai.Toggler.RecordConsent();
            }
            var r = await ai.DraftSelected(CancellationToken.None);
            _s.Balloons.PostUserFeedback(r.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _s.Balloons.PostUserFeedback(AiMenuTexts.ToggleFailed(ex.Message));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _s.Balloons.PostUserFeedback(DraftTexts.ManualFailed("something went wrong (" + ex.GetType().Name + ")"));
        }
    }

    /// <summary>Show Drafts in Outlook, also for a click on the "I drafted n replies" balloon.</summary>
    private async void ShowDraftsInOutlook()
    {
        var ai = _s.Ai;
        if (ai is null)
        {
            return;
        }
        try
        {
            if (!await ai.ShowDrafts(CancellationToken.None))
            {
                _s.Balloons.PostUserFeedback(BalloonTexts.NoOutlookWindow);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _s.Balloons.PostUserFeedback(BalloonTexts.NoOutlookWindow);
        }
    }

    private void AskRebuild()
    {
        if (_s.Rebuild is null)
        {
            return;
        }
        var mine = new TaskDialogButton("My mailbox");
        var all = new TaskDialogButton("All mailboxes");
        var cancel = new TaskDialogButton("Cancel");
        var page = new TaskDialogPage
        {
            Caption = BalloonTexts.Title,
            Heading = "Rebuild the email index?",
            Text = "Clippy deletes the index and reads your mail from Outlook again. Search finds less until indexing is done.",
            AllowCancel = true,
            DefaultButton = cancel,
        };
        page.Buttons.Add(mine);
        if (_s.GetSettings().SharedMailboxes.Any(m => m is not null && m.Enabled))
        {
            page.Buttons.Add(all);
        }
        page.Buttons.Add(cancel);
        var choice = TaskDialog.ShowDialog(page);
        if (choice == mine)
        {
            _ = RunRebuild("");
        }
        else if (choice == all)
        {
            _ = RunRebuild("all");
        }
    }

    private async Task RunRebuild(string key)
    {
        try
        {
            await _s.Rebuild!(key);
        }
        catch (Exception ex)
        {
            PostToUi(() => _s.Balloons.PostUserFeedback("Couldn't rebuild the index: " + ex.Message));
        }
    }

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fills the submenu from settings and the last known store list at once, then refreshes the store list on
    /// the Outlook worker; the UI thread never waits for COM.
    /// </summary>
    private void RebuildShared()
    {
        var outlookUp = !_s.IsOutlookUnavailable();
        FillShared(outlookUp ? _mounted : null);
        if (!outlookUp || _s.ListMounted is null || Interlocked.Exchange(ref _listing, 1) == 1)
        {
            return;
        }
        _ = RefreshMountedAsync();
    }

    private async Task RefreshMountedAsync()
    {
        try
        {
            var list = await _s.ListMounted!().WaitAsync(ListTimeout).ConfigureAwait(false);
            PostToUi(() =>
            {
                _mounted = list;
                if (_shared.DropDown.Visible)
                {
                    FillShared(_mounted);
                }
            });
        }
        catch (Exception)
        {
            // Outlook is slow or gone: keep what the menu shows. The next opening tries again.
        }
        finally
        {
            Interlocked.Exchange(ref _listing, 0);
        }
    }

    private void FillShared(IReadOnlyList<MailboxStore>? mounted)
    {
        _shared.DropDownItems.Clear();
        var model = SharedMailboxMenu.Build(mounted, _s.GetSettings().SharedMailboxes);
        foreach (var e in model.Entries)
        {
            var entry = e;
            var item = new ToolStripMenuItem(entry.Label) { Checked = entry.Checked, CheckOnClick = false, Enabled = _s.SharedToggler is not null };
            item.Click += (_, _) => ToggleShared(entry);
            _shared.DropDownItems.Add(item);
        }
        if (model.StartOutlookHint)
        {
            _shared.DropDownItems.Add(new ToolStripMenuItem(SharedMailboxMenu.StartOutlookHintText) { Enabled = false });
        }
        if (model.Entries.Count > 0 || model.StartOutlookHint)
        {
            _shared.DropDownItems.Add(new ToolStripSeparator());
        }
        _shared.DropDownItems.Add(new ToolStripMenuItem("Manage in Settings…", null, (_, _) => ShowSettings(SettingsTab.Index)));
    }

    private void ToggleShared(SharedMenuEntry entry)
    {
        var toggler = _s.SharedToggler;
        if (toggler is null)
        {
            return;
        }
        try
        {
            if (!entry.Checked)
            {
                toggler.Check(entry.Smtp, entry.DisplayName);
                return;
            }
            var name = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Smtp : entry.DisplayName;
            var delete = new TaskDialogButton("Delete its index");
            var keep = new TaskDialogButton("Keep index file");
            var cancel = new TaskDialogButton("Cancel");
            var page = new TaskDialogPage
            {
                Caption = BalloonTexts.Title,
                Heading = $"Stop indexing {name}?",
                Text = "The mailbox is switched off either way. Deleting the index frees disk space; checking it again later starts from scratch.",
                AllowCancel = true,
                DefaultButton = keep,
            };
            page.Buttons.Add(delete);
            page.Buttons.Add(keep);
            page.Buttons.Add(cancel);
            var choice = TaskDialog.ShowDialog(page);
            if (choice == delete)
            {
                toggler.Uncheck(entry.Smtp, true);
            }
            else if (choice == keep)
            {
                toggler.Uncheck(entry.Smtp, false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _s.Balloons.PostUserFeedback("Couldn't change that mailbox: " + ex.Message);
        }
    }

    private void CopyToClipboard(Func<string> text, bool announce)
    {
        try
        {
            Clipboard.SetText(text());
            if (announce)
            {
                _s.Balloons.PostUserFeedback(BalloonTexts.Copied);
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _s.Balloons.PostUserFeedback("Couldn't reach the clipboard — try again.");
        }
    }

    private void RegenerateToken()
    {
        if (_s.RegenerateToken is null)
        {
            return;
        }
        var answer = MessageBox.Show(
            "Regenerate the auth token?\n\nThe current token stops working immediately. Claude Code must be set up again with the new \"claude mcp add\" command.",
            BalloonTexts.Title,
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.OK)
        {
            var t = _s.RegenerateToken();
            _s.Token.Set(t);
        }
    }

    /// <summary>Hands a folder, a file or an https URL to the Windows shell (Explorer, the user's default editor or
    /// browser). Never a command line, never arguments. The one Process.Start in TrayApp.</summary>
    internal static void ShellOpen(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        ShellOpen(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _s.Hub.Changed -= OnHubChanged;
            if (_s.Attention is not null)
            {
                _s.Attention.Changed -= OnHubChanged;
            }
            _s.Balloons.Shown -= OnBalloon;
            _animTimer.Stop();
            _animTimer.Dispose();
            _houseTimer.Stop();
            _houseTimer.Dispose();
            _activity?.Dispose();
            _about?.Dispose();
            _settingsForm?.Dispose();
            _notify.Visible = false;
            _notify.Dispose();
            _menu.Dispose();
            _icons.Dispose();
            _badge?.Dispose();
        }
        base.Dispose(disposing);
    }
}
