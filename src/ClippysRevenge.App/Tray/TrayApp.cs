using System.ComponentModel;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using ClippysRevenge.Core;
using Timer = System.Windows.Forms.Timer;

namespace ClippysRevenge.App.Tray;

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
    Func<string, Task>? Rebuild = null);

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
    private readonly ToolStripMenuItem _startWithWindows = new("Start with Windows") { CheckOnClick = false };

    private TrayIconKey _shownKey = TrayIconKey.Idle;
    private bool _animated;
    private int _frame;
    private int _renderQueued;
    private volatile bool _disposed;

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

        BuildMenu();

        _notify = new NotifyIcon
        {
            Icon = _icons.Get(TrayIconKey.Idle),
            Text = "Clippy — idle",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => ShowActivity();

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
        _s.Balloons.Shown += OnBalloon;
        Render();
    }

    /// <summary>Any thread; opens the Settings window on the UI thread (stub until M9.2).</summary>
    public void ShowSettings() => PostToUi(() =>
    {
        // TODO(M9.2): show SettingsForm.
    });

    /// <summary>Any thread; opens the Activity window on the UI thread (stub until M9.3).</summary>
    public void ShowActivity() => PostToUi(() =>
    {
        // TODO(M9.3): show ActivityForm.
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

        var view = _presenter.Update(state, progress, _s.GetStats(), lastTool);
        if (view.Animated)
        {
            if (!_animated)
            {
                _animated = true;
                _frame = 0;
                SetIcon(TrayIconKey.Backfill0);
                _animTimer.Start();
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
        if (key == _shownKey)
        {
            return;
        }
        _shownKey = key;
        _notify.Icon = _icons.Get(key);
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
        activity.Click += (_, _) => ShowActivity(); // TODO(M9.3)
        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings(); // TODO(M9.2)
        var logs = new ToolStripMenuItem("Open log folder");
        logs.Click += (_, _) => OpenFolder(_s.LogFolder);
        var data = new ToolStripMenuItem("Open data folder");
        data.Click += (_, _) => OpenFolder(_s.DataFolder);

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
        _shared.DropDownItems.Add(new ToolStripMenuItem("Manage in Settings…", null, (_, _) => ShowSettings()));
        _menu.Opening += (_, _) =>
        {
            _startWithWindows.Checked = _s.GetSettings().StartWithWindows;
            _syncNow.Enabled = _s.SyncNow is not null && !_s.GetStats().Paused;
        };

        _menu.Items.AddRange([
            _status1, _status2, new ToolStripSeparator(),
            _startOutlook, _syncNow, _pause, _rebuild, _shared, new ToolStripSeparator(),
            copyCmd, copyToken, regen, new ToolStripSeparator(),
            activity, settings, logs, data, new ToolStripSeparator(),
            _startWithWindows, exit,
        ]);
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

    private void RebuildShared()
    {
        _shared.DropDownItems.Clear();
        var boxes = _s.GetSettings().SharedMailboxes;
        foreach (var m in boxes)
        {
            // TODO(M9.1): toggling.
            _shared.DropDownItems.Add(new ToolStripMenuItem(m.MenuLabel) { Checked = m.Enabled, Enabled = false });
        }
        if (boxes.Count > 0)
        {
            _shared.DropDownItems.Add(new ToolStripSeparator());
        }
        _shared.DropDownItems.Add(new ToolStripMenuItem("Manage in Settings…", null, (_, _) => ShowSettings()));
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

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _s.Hub.Changed -= OnHubChanged;
            _s.Balloons.Shown -= OnBalloon;
            _animTimer.Stop();
            _animTimer.Dispose();
            _houseTimer.Stop();
            _houseTimer.Dispose();
            _notify.Visible = false;
            _notify.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
