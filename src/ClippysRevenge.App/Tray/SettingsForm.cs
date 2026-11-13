using System.Globalization;
using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Timer = System.Windows.Forms.Timer;

namespace ClippysRevenge.App.Tray;

internal sealed record SettingsFormServices(
    Func<Settings> GetSettings,
    TokenHolder Tokens,
    SettingsApplier Applier,
    Func<int?> ServerPort,
    OutlookWorker Worker,
    OutlookStores Stores,
    MailboxIndexes? Indexes,
    Func<string, Task>? Rebuild,
    Func<bool> IsOutlookUnavailable,
    TimeZoneInfo TimeZone);

/// <summary>
/// Settings window (SPEC 6.3). Edits a staged <see cref="SettingsEditor"/>; nothing reaches the live settings
/// before OK or Apply. Nothing here waits for COM on the UI thread: Outlook-backed lists load in the background.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly TimeSpan ComLimit = TimeSpan.FromSeconds(15);

    private readonly SettingsFormServices _s;
    private SettingsEditor _editor;
    private readonly CancellationTokenSource _cts = new();
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly Timer _statsTimer = new() { Interval = 5000 };
    private bool _binding;
    private bool _busy;
    private bool _tokenShown;
    private string _key = "";
    private int _treeGen, _statsGen;
    private bool _treeReady;

    private readonly TabControl _tabs = new() { Location = new Point(8, 8), Size = new Size(684, 500) };
    private readonly Button _ok = new() { Text = "OK" };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    private readonly Button _apply = new() { Text = "Apply" };
    private readonly Label _status = new() { AutoEllipsis = true };

    // Connection
    private readonly TextBox _port = new();
    private readonly TextBox _token = new() { ReadOnly = true, UseSystemPasswordChar = true };
    private readonly Button _showToken = new() { Text = "Show" };
    private readonly Button _copyToken = new() { Text = "Copy" };
    private readonly Button _regen = new() { Text = "Regenerate…" };
    private readonly TextBox _command = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _copyCommand = new() { Text = "Copy command" };
    private readonly Button _test = new() { Text = "Test connection" };
    private readonly Label _testResult = new();
    private readonly Label _testNote = new() { ForeColor = SystemColors.GrayText };
    private readonly TextBox _wsl = new() { ReadOnly = true, Multiline = true };

    // Index
    private readonly ComboBox _picker = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _add = new() { Text = "Add…" };
    private readonly Button _remove = new() { Text = "Remove…" };
    private readonly ContextMenuStrip _addMenu = new();
    private readonly TextBox _retention = new();
    private readonly CheckBox _useGlobal = new() { Text = "Use global" };
    private readonly TreeView _tree = new() { CheckBoxes = true, HideSelection = false };
    private readonly Label _treeNote = new() { ForeColor = SystemColors.GrayText };
    private readonly Label _stats = new();
    private readonly Button _rebuild = new() { Text = "Rebuild…" };
    private readonly TextBox _sync = new();

    // Scheduling
    private readonly TextBox _workStart = new(), _workEnd = new(), _timeZone = new(), _inviteTimeout = new();
    private readonly CheckBox _businessDays = new() { Text = "Business days only" };
    private readonly CheckBox _tentative = new() { Text = "Tentative counts as busy" };
    private readonly Label _tzAuto = new() { ForeColor = SystemColors.GrayText };
    private readonly RadioButton _inviteSend = new() { Text = "Send (with confirmation)" };
    private readonly RadioButton _inviteDraft = new() { Text = "Draft only" };
    private readonly RadioButton _inviteOff = new() { Text = "Disabled" };

    // General
    private readonly TextBox _attachDir = new();
    private readonly Button _browse = new() { Text = "Browse…" };
    private readonly ListBox _allowed = new();
    private readonly Button _allowedAdd = new() { Text = "Add…" };
    private readonly Button _allowedRemove = new() { Text = "Remove" };
    private readonly CheckBox _startWin = new() { Text = "Start with Windows" };
    private readonly CheckBox _notify = new() { Text = "Notify on errors" };
    private readonly ComboBox _logLevel = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    public SettingsForm(SettingsFormServices services, SettingsTab initialTab = SettingsTab.Connection)
    {
        ArgumentNullException.ThrowIfNull(services);
        _s = services;
        _editor = new SettingsEditor(_s.GetSettings());

        Text = BalloonTexts.Title + " — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 560);
        AcceptButton = null;
        CancelButton = _cancel;

        BuildConnection();
        BuildIndex();
        BuildScheduling();
        BuildGeneral();

        _ok.SetBounds(440, 522, 76, 28);
        _cancel.SetBounds(522, 522, 76, 28);
        _apply.SetBounds(604, 522, 76, 28);
        _status.SetBounds(12, 526, 420, 22);
        Controls.AddRange([_tabs, _ok, _cancel, _apply, _status]);

        _ok.Click += async (_, _) => await ApplyAsync(close: true);
        _apply.Click += async (_, _) => await ApplyAsync(close: false);
        _statsTimer.Tick += (_, _) =>
        {
            if (_tabs.SelectedIndex == (int)SettingsTab.Index) RefreshStats();
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == (int)SettingsTab.Index) RefreshStats();
        };
        _statsTimer.Start();

        BindAll(keepKey: null);
        SelectTab(initialTab);
    }

    public void SelectTab(SettingsTab tab) => _tabs.SelectedIndex = (int)tab;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _statsTimer.Stop();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _statsTimer.Dispose();
            _errors.Dispose();
            _addMenu.Dispose();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- layout helpers ----

    private static TabPage NewPage(string title)
    {
        return new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true };
    }

    private static Label Lbl(Control parent, string text, int x, int y, int w = 200)
    {
        var l = new Label { Text = text, AutoSize = false };
        l.SetBounds(x, y + 4, w, 20);
        parent.Controls.Add(l);
        return l;
    }

    private static T Put<T>(Control parent, T c, int x, int y, int w, int h) where T : Control
    {
        c.SetBounds(x, y, w, h);
        parent.Controls.Add(c);
        return c;
    }

    private void BuildConnection()
    {
        var p = NewPage("Connection");
        Lbl(p, "Port", 8, 12, 120);
        Put(p, _port, 140, 12, 80, 24);
        Lbl(p, "Auth token", 8, 48, 120);
        Put(p, _token, 140, 48, 330, 24);
        Put(p, _showToken, 478, 47, 54, 26);
        Put(p, _copyToken, 536, 47, 54, 26);
        Put(p, _regen, 140, 80, 110, 26);
        Lbl(p, "Claude Code command", 8, 120, 160);
        Put(p, _command, 8, 144, 650, 72);
        Put(p, _copyCommand, 8, 222, 120, 26);
        Put(p, _test, 8, 262, 120, 26);
        Put(p, _testResult, 138, 266, 520, 22);
        Put(p, _testNote, 138, 290, 520, 22);
        Lbl(p, "From WSL", 8, 322, 160);
        Put(p, _wsl, 8, 346, 650, 100);
        _tabs.TabPages.Add(p);

        _port.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _editor.TrySetInt("Port", _port.Text, v => _editor.Draft.Port = v);
            RefreshConnectionTexts();
        };
        _port.Validated += (_, _) => RefreshErrors();
        _showToken.Click += (_, _) =>
        {
            _tokenShown = !_tokenShown;
            RefreshConnectionTexts();
        };
        _copyToken.Click += (_, _) => CopyText(TokenShown(), null);
        _copyCommand.Click += (_, _) => CopyText(FullCommand(), BalloonTexts.Copied);
        _regen.Click += (_, _) =>
        {
            var answer = MessageBox.Show(this, SettingsTexts.RegenerateQuestion, BalloonTexts.Title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            _editor.RegenerateToken();
            RefreshConnectionTexts();
        };
        _test.Click += async (_, _) => await RunTestAsync();
    }

    private void BuildIndex()
    {
        var p = NewPage("Index");
        _picker.DisplayMember = nameof(SettingsMailboxChoice.Label);
        Lbl(p, "Mailbox", 8, 12, 80);
        Put(p, _picker, 96, 12, 330, 24);
        Put(p, _add, 434, 11, 70, 26);
        Put(p, _remove, 510, 11, 80, 26);
        Lbl(p, "Retention (months)", 8, 48, 130);
        Put(p, _retention, 140, 48, 60, 24);
        Put(p, _useGlobal, 210, 48, 120, 24);
        Lbl(p, "Indexed folders", 8, 84, 130);
        Put(p, _tree, 8, 108, 650, 190);
        Put(p, _treeNote, 8, 300, 650, 20);
        Put(p, _stats, 8, 326, 650, 22);
        Put(p, _rebuild, 8, 352, 90, 26);
        Lbl(p, "Sync interval (seconds)", 8, 396, 150);
        Put(p, _sync, 170, 396, 70, 24);
        _tabs.TabPages.Add(p);

        _picker.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _picker.SelectedIndex < 0) return;
            _key = ((SettingsMailboxChoice)_picker.SelectedItem!).Key;
            BindMailbox();
        };
        _add.Click += (_, _) => OpenAddMenu();
        _remove.Click += (_, _) => RemoveSelectedMailbox();
        _retention.TextChanged += (_, _) =>
        {
            if (_binding) return;
            var sm = CurrentShared();
            if (sm is null) _editor.TrySetInt("RetentionMonths", _retention.Text, v => _editor.Draft.RetentionMonths = v);
            else _editor.TrySetInt($"SharedMailboxes[{SharedIndex(sm)}].RetentionMonths", _retention.Text, v => sm.RetentionMonths = v);
        };
        _retention.Validated += (_, _) => RefreshErrors();
        _useGlobal.CheckedChanged += (_, _) =>
        {
            if (_binding) return;
            var sm = CurrentShared();
            if (sm is null) return;
            sm.RetentionMonths = _useGlobal.Checked ? null : _editor.Draft.RetentionMonths;
            BindRetention();
            RefreshErrors();
        };
        _tree.AfterCheck += (_, _) => WriteFolders();
        _sync.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _editor.TrySetInt("SyncIntervalSeconds", _sync.Text, v => _editor.Draft.SyncIntervalSeconds = v);
        };
        _sync.Validated += (_, _) => RefreshErrors();
        _rebuild.Click += async (_, _) => await RebuildAsync();
    }

    private void BuildScheduling()
    {
        var p = NewPage("Scheduling");
        Lbl(p, "Work hours: start", 8, 12, 130);
        Put(p, _workStart, 150, 12, 50, 24);
        Lbl(p, "end", 210, 12, 30);
        Put(p, _workEnd, 244, 12, 50, 24);
        Put(p, _businessDays, 8, 48, 250, 24);
        Put(p, _tentative, 8, 76, 250, 24);
        Lbl(p, "Time-zone label", 8, 112, 130);
        Put(p, _timeZone, 150, 112, 100, 24);
        Put(p, _tzAuto, 260, 116, 300, 20);
        Lbl(p, "Meeting invites", 8, 152, 130);
        Put(p, _inviteSend, 150, 152, 250, 24);
        Put(p, _inviteDraft, 150, 178, 250, 24);
        Put(p, _inviteOff, 150, 204, 250, 24);
        Lbl(p, "Confirmation timeout (s)", 8, 244, 160);
        Put(p, _inviteTimeout, 180, 244, 60, 24);
        _tabs.TabPages.Add(p);

        _workStart.TextChanged += (_, _) => IntField("WorkStartHour", _workStart, v => _editor.Draft.WorkStartHour = v);
        _workEnd.TextChanged += (_, _) => IntField("WorkEndHour", _workEnd, v => _editor.Draft.WorkEndHour = v);
        _inviteTimeout.TextChanged += (_, _) => IntField("InviteConfirmTimeoutSeconds", _inviteTimeout, v => _editor.Draft.InviteConfirmTimeoutSeconds = v);
        foreach (var t in new[] { _workStart, _workEnd, _inviteTimeout, _timeZone }) t.Validated += (_, _) => RefreshErrors();
        _businessDays.CheckedChanged += (_, _) => { if (!_binding) _editor.Draft.BusinessDaysOnly = _businessDays.Checked; };
        _tentative.CheckedChanged += (_, _) => { if (!_binding) _editor.Draft.TentativeIsBusy = _tentative.Checked; };
        _timeZone.TextChanged += (_, _) => { if (!_binding) _editor.Draft.TimeZoneLabel = _timeZone.Text; };
        void Mode(RadioButton r, MeetingInviteMode m) =>
            r.CheckedChanged += (_, _) => { if (!_binding && r.Checked) _editor.Draft.MeetingInviteMode = m; };
        Mode(_inviteSend, MeetingInviteMode.Send);
        Mode(_inviteDraft, MeetingInviteMode.DraftOnly);
        Mode(_inviteOff, MeetingInviteMode.Disabled);
    }

    private void BuildGeneral()
    {
        var p = NewPage("General");
        Lbl(p, "Attachment folder", 8, 12, 130);
        Put(p, _attachDir, 8, 36, 560, 24);
        Put(p, _browse, 576, 35, 80, 26);
        Lbl(p, "Allowed folders", 8, 72, 130);
        Put(p, _allowed, 8, 96, 560, 110);
        Put(p, _allowedAdd, 576, 96, 80, 26);
        Put(p, _allowedRemove, 576, 126, 80, 26);
        var note = Lbl(p, "The attachment folder is always allowed.", 8, 208, 400);
        note.ForeColor = SystemColors.GrayText;
        Put(p, _startWin, 8, 244, 250, 24);
        Put(p, _notify, 8, 272, 250, 24);
        Lbl(p, "Log level", 8, 312, 80);
        Put(p, _logLevel, 100, 312, 130, 24);
        var logNote = Lbl(p, @"Applies at once to the log in %LOCALAPPDATA%\ClippysRevenge\logs.", 240, 312, 420);
        logNote.ForeColor = SystemColors.GrayText;
        _tabs.TabPages.Add(p);

        foreach (var l in SettingsTexts.LogLevels) _logLevel.Items.Add(l.ToString());
        _attachDir.TextChanged += (_, _) => { if (!_binding) _editor.Draft.AttachmentDir = _attachDir.Text; };
        _attachDir.Validated += (_, _) => RefreshErrors();
        _browse.Click += (_, _) =>
        {
            var dir = PickFolder(Directory.Exists(_attachDir.Text) ? _attachDir.Text : null);
            if (dir is not null) _attachDir.Text = dir;
            RefreshErrors();
        };
        _allowedAdd.Click += (_, _) =>
        {
            var dir = PickFolder(null);
            if (dir is null) return;
            _editor.Draft.AllowedFolders.Add(dir);
            _allowed.Items.Add(dir);
            RefreshErrors();
        };
        _allowedRemove.Click += (_, _) =>
        {
            var i = _allowed.SelectedIndex;
            if (i < 0) return;
            _editor.Draft.AllowedFolders.RemoveAt(i);
            _allowed.Items.RemoveAt(i);
            RefreshErrors();
        };
        _startWin.CheckedChanged += (_, _) => { if (!_binding) _editor.Draft.StartWithWindows = _startWin.Checked; };
        _notify.CheckedChanged += (_, _) => { if (!_binding) _editor.Draft.NotifyOnErrors = _notify.Checked; };
        _logLevel.SelectedIndexChanged += (_, _) =>
        {
            if (!_binding && _logLevel.SelectedIndex >= 0) _editor.Draft.LogLevel = SettingsTexts.LogLevels[_logLevel.SelectedIndex];
        };
    }

    private string? PickFolder(string? initial)
    {
        using var d = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Choose a folder" };
        if (initial is not null) d.InitialDirectory = initial;
        return d.ShowDialog(this) == DialogResult.OK ? d.SelectedPath : null;
    }

    private void IntField(string field, TextBox box, Action<int> assign)
    {
        if (_binding) return;
        _editor.TrySetInt(field, box.Text, assign);
    }

    // ---- binding ----

    private void BindAll(string? keepKey)
    {
        _binding = true;
        try
        {
            var d = _editor.Draft;
            _port.Text = d.Port.ToString(CultureInfo.InvariantCulture);
            _sync.Text = d.SyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            _workStart.Text = d.WorkStartHour.ToString(CultureInfo.InvariantCulture);
            _workEnd.Text = d.WorkEndHour.ToString(CultureInfo.InvariantCulture);
            _inviteTimeout.Text = d.InviteConfirmTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            _businessDays.Checked = d.BusinessDaysOnly;
            _tentative.Checked = d.TentativeIsBusy;
            _timeZone.Text = d.TimeZoneLabel;
            _tzAuto.Text = SettingsTexts.TimeZoneAutomatic(TimeZoneLabels.For(_s.TimeZone));
            _inviteSend.Checked = d.MeetingInviteMode == MeetingInviteMode.Send;
            _inviteDraft.Checked = d.MeetingInviteMode == MeetingInviteMode.DraftOnly;
            _inviteOff.Checked = d.MeetingInviteMode == MeetingInviteMode.Disabled;
            _attachDir.Text = d.AttachmentDir;
            _allowed.Items.Clear();
            foreach (var f in d.AllowedFolders) _allowed.Items.Add(f);
            _startWin.Checked = d.StartWithWindows;
            _notify.Checked = d.NotifyOnErrors;
            _logLevel.SelectedIndex = Math.Max(0, SettingsTexts.LogLevels.ToList().IndexOf(d.LogLevel));
            _wsl.Text = SettingsTexts.WslHelp(_s.GetSettings().Port);
            FillPicker(keepKey ?? _key);
        }
        finally
        {
            _binding = false;
        }
        RefreshConnectionTexts();
        BindMailbox();
        RefreshErrors();
    }

    private void FillPicker(string key)
    {
        var choices = _editor.MailboxChoices();
        _picker.Items.Clear();
        foreach (var c in choices) _picker.Items.Add(c);
        var idx = choices.ToList().FindIndex(c => c.Key == key);
        if (idx < 0) idx = 0;
        _picker.SelectedIndex = idx;
        _key = choices[idx].Key;
    }

    private SharedMailbox? CurrentShared() => _key.Length == 0 ? null : _editor.Draft.FindSharedMailbox(_key);

    private int SharedIndex(SharedMailbox sm) => _editor.Draft.SharedMailboxes.IndexOf(sm);

    private void BindMailbox()
    {
        _remove.Enabled = _key.Length > 0;
        _rebuild.Enabled = _s.Rebuild is not null && (_key.Length == 0 || IsEnabledLive(_key));
        BindRetention();
        LoadFolders();
        RefreshStats();
        RefreshErrors();
    }

    private bool IsEnabledLive(string key) =>
        _s.GetSettings().FindSharedMailbox(key) is { Enabled: true };

    private void BindRetention()
    {
        var prev = _binding;
        _binding = true;
        try
        {
            var sm = CurrentShared();
            var d = _editor.Draft;
            _useGlobal.Visible = sm is not null;
            if (sm is null)
            {
                _retention.Enabled = true;
                _retention.Text = d.RetentionMonths.ToString(CultureInfo.InvariantCulture);
                _editor.TrySetInt("RetentionMonths", _retention.Text, v => d.RetentionMonths = v);
            }
            else
            {
                var field = $"SharedMailboxes[{SharedIndex(sm)}].RetentionMonths";
                _useGlobal.Checked = sm.RetentionMonths is null;
                _retention.Enabled = sm.RetentionMonths is not null;
                _retention.Text = (sm.RetentionMonths ?? d.RetentionMonths).ToString(CultureInfo.InvariantCulture);
                _editor.TrySetInt(field, _retention.Text, v => sm.RetentionMonths = sm.RetentionMonths is null ? null : v);
            }
        }
        finally
        {
            _binding = prev;
        }
    }

    private List<string> CurrentFolderList() => CurrentShared()?.IndexedFolders ?? _editor.Draft.IndexedFolders;

    // ---- folder tree (background load) ----

    private async void LoadFolders()
    {
        var gen = ++_treeGen;
        var key = _key;
        _treeReady = false;
        _treeNote.Text = "";
        _tree.Nodes.Clear();
        _tree.Nodes.Add(new TreeNode(SettingsTexts.LoadingFolders) { ForeColor = SystemColors.GrayText });
        IReadOnlyList<string>? available = null;
        try
        {
            if (!_s.IsOutlookUnavailable())
            {
                available = await _s.Worker.RunAsync(app => _s.Stores.ListMailFolders(app, key), WorkPriority.Interactive, _cts.Token)
                    .WaitAsync(ComLimit, _cts.Token);
            }
        }
        catch (Exception)
        {
            available = null;
        }
        if (IsDisposed || gen != _treeGen) return;
        try
        {
            RenderTree(available, CurrentFolderList());
            _treeNote.Text = available is null ? SettingsTexts.FoldersUnavailable : "";
        }
        catch (Exception ex)
        {
            _status.Text = "Couldn't show the folders: " + ex.Message;
        }
    }

    private void RenderTree(IReadOnlyList<string>? available, IReadOnlyList<string> configured)
    {
        var nodes = FolderTree.Build(available, configured);
        _tree.BeginUpdate();
        try
        {
            _treeReady = false;
            _tree.Nodes.Clear();
            void Add(TreeNodeCollection into, FolderTreeNode n)
            {
                var t = new TreeNode(n.Missing ? n.Name + SettingsTexts.NotFoundSuffix : n.Name) { Tag = n.Path, Checked = n.Checked };
                into.Add(t);
                foreach (var c in n.Children) Add(t.Nodes, c);
            }
            foreach (var n in nodes) Add(_tree.Nodes, n);
            _tree.ExpandAll();
            _treeReady = true;
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private void WriteFolders()
    {
        if (_binding || !_treeReady) return;
        var paths = new List<string>();
        void Walk(TreeNodeCollection c)
        {
            foreach (TreeNode n in c)
            {
                if (n.Checked && n.Tag is string p) paths.Add(p);
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
        var sm = CurrentShared();
        if (sm is null) _editor.Draft.IndexedFolders = paths;
        else sm.IndexedFolders = paths;
        RefreshErrors();
    }

    // ---- stats ----

    private async void RefreshStats()
    {
        var gen = ++_statsGen;
        var key = _key;
        var ix = _s.Indexes;
        string text;
        if (ix is null)
        {
            text = IndexStatsText.NotIndexed;
        }
        else
        {
            try
            {
                var status = await Task.Run(() => ix.For(key).Status(), _cts.Token);
                text = IndexStatsText.Format(status, DateTimeOffset.UtcNow, _s.TimeZone);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ModelContextProtocol.McpException)
            {
                text = IndexStatsText.NotIndexed;
            }
            catch (Exception ex)
            {
                text = "Stats unavailable: " + ex.Message;
            }
        }
        if (IsDisposed || gen != _statsGen) return;
        _stats.Text = text;
    }

    // ---- mailbox add / remove / rebuild ----

    private async void OpenAddMenu()
    {
        _addMenu.Items.Clear();
        _addMenu.Items.Add(new ToolStripMenuItem("Loading…") { Enabled = false });
        _addMenu.Show(_add, new Point(0, _add.Height));
        var items = new List<ToolStripItem>();
        try
        {
            if (_s.IsOutlookUnavailable())
            {
                items.Add(Hint(SettingsTexts.StartOutlookForMailboxes));
            }
            else
            {
                var stores = await _s.Worker.RunAsync(app => _s.Stores.List(app), WorkPriority.Interactive, _cts.Token)
                    .WaitAsync(ComLimit, _cts.Token);
                var enabled = _editor.MailboxChoices().Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var m in StoreCatalog.SharedOnly(stores))
                {
                    if (enabled.Contains(m.Smtp)) continue;
                    var store = m;
                    var label = string.IsNullOrWhiteSpace(store.DisplayName) ? store.Smtp : $"{store.DisplayName} <{store.Smtp}>";
                    var item = new ToolStripMenuItem(label);
                    item.Click += (_, _) => AddMailbox(store);
                    items.Add(item);
                }
                if (items.Count == 0) items.Add(Hint(SettingsTexts.NoOtherMailboxes));
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            items.Clear();
            items.Add(Hint(SettingsTexts.StartOutlookForMailboxes));
        }
        if (IsDisposed) return;
        _addMenu.Items.Clear();
        _addMenu.Items.AddRange([.. items]);
    }

    private static ToolStripMenuItem Hint(string text) => new(text) { Enabled = false };

    private void AddMailbox(MailboxStore m)
    {
        try
        {
            _editor.AddMailbox(m.Smtp, m.DisplayName);
            FillPickerKeeping(m.Smtp);
        }
        catch (ArgumentException ex)
        {
            _status.Text = "Couldn't add that mailbox: " + ex.Message;
        }
    }

    private void FillPickerKeeping(string key)
    {
        _binding = true;
        try { FillPicker(key); }
        finally { _binding = false; }
        BindMailbox();
    }

    private void RemoveSelectedMailbox()
    {
        var sm = CurrentShared();
        if (sm is null) return;
        var label = sm.MenuLabel;
        var delete = new TaskDialogButton(SettingsTexts.DeleteIndexButton);
        var keep = new TaskDialogButton(SettingsTexts.KeepIndexButton);
        var cancel = new TaskDialogButton("Cancel");
        var page = new TaskDialogPage
        {
            Caption = BalloonTexts.Title,
            Heading = SettingsTexts.StopIndexingQuestion(label),
            Text = "The mailbox is switched off either way. Deleting the index frees disk space; adding it again later starts from scratch. This takes effect when you press OK or Apply.",
            AllowCancel = true,
            DefaultButton = cancel,
        };
        page.Buttons.Add(delete);
        page.Buttons.Add(keep);
        page.Buttons.Add(cancel);
        var choice = TaskDialog.ShowDialog(this, page);
        if (choice != delete && choice != keep) return;
        if (_editor.RemoveMailbox(sm.Smtp, choice == delete)) FillPickerKeeping("");
    }

    private async Task RebuildAsync()
    {
        if (_s.Rebuild is null) return;
        var label = ((SettingsMailboxChoice?)_picker.SelectedItem)?.Label ?? SettingsEditor.OwnMailboxLabel;
        var answer = MessageBox.Show(this, SettingsTexts.RebuildQuestion(label), BalloonTexts.Title,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        var key = _key;
        _rebuild.Enabled = false;
        try
        {
            await _s.Rebuild(key);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "Couldn't rebuild the index: " + ex.Message;
        }
        finally
        {
            if (!IsDisposed) _rebuild.Enabled = true;
        }
    }

    // ---- connection tab ----

    private string TokenShown() => _editor.PendingToken ?? _s.Tokens.Current;

    private int ShownPort()
    {
        var live = _s.GetSettings().Port;
        return int.TryParse(_port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535
            ? p : live;
    }

    private string FullCommand() => TrayText.McpAddCommand(ShownPort(), TokenShown());

    private void RefreshConnectionTexts()
    {
        _token.Text = TokenShown();
        _token.UseSystemPasswordChar = !_tokenShown;
        _showToken.Text = _tokenShown ? "Hide" : "Show";
        _command.Text = _tokenShown ? FullCommand() : SettingsTexts.MaskedMcpAddCommand(ShownPort());
        var changes = _editor.Changes(_s.GetSettings());
        _testNote.Text = changes.BreaksClaudeConfig ? "Testing the running server. Press Apply first to test your changes." : "";
    }

    private void CopyText(string text, string? announce)
    {
        try
        {
            Clipboard.SetText(text);
            _status.Text = announce ?? "";
        }
        catch (ExternalException)
        {
            _status.Text = "Couldn't reach the clipboard — try again.";
        }
    }

    private async Task RunTestAsync()
    {
        _test.Enabled = false;
        _testResult.Text = "Testing…";
        _testResult.ForeColor = SystemColors.ControlText;
        try
        {
            using var tester = new ConnectionTester();
            var r = await tester.TestAsync(_s.ServerPort(), _s.Tokens.Current, _cts.Token);
            if (IsDisposed) return;
            _testResult.Text = r.Message;
            _testResult.ForeColor = r.Ok ? Color.DarkGreen : Color.Firebrick;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _testResult.Text = "✗ " + ex.GetType().Name;
                _testResult.ForeColor = Color.Firebrick;
            }
        }
        finally
        {
            if (!IsDisposed) _test.Enabled = true;
        }
    }

    // ---- errors and apply ----

    private Control ControlFor(SettingsError e)
    {
        var f = e.Field;
        var shared = SettingsEditor.SharedMailboxIndexFor(f);
        if (shared is { } i)
        {
            var cur = CurrentShared();
            if (cur is not null && SharedIndex(cur) == i)
            {
                if (f.Contains(".RetentionMonths", StringComparison.Ordinal)) return _retention;
                if (f.Contains(".IndexedFolders", StringComparison.Ordinal)) return _tree;
            }
            return _picker;
        }
        if (f.StartsWith("IndexedFolders", StringComparison.Ordinal)) return _tree;
        if (f.StartsWith("AllowedFolders", StringComparison.Ordinal)) return _allowed;
        return f switch
        {
            "Port" => _port,
            "RetentionMonths" => _retention,
            "SyncIntervalSeconds" => _sync,
            "WorkStartHour" => _workStart,
            "WorkEndHour" => _workEnd,
            "TimeZoneLabel" => _timeZone,
            "MeetingInviteMode" => _inviteSend,
            "InviteConfirmTimeoutSeconds" => _inviteTimeout,
            "AttachmentDir" => _attachDir,
            "LogLevel" => _logLevel,
            _ => _status,
        };
    }

    private void ShowErrors(IReadOnlyList<SettingsError> errors)
    {
        foreach (var c in new Control[] { _port, _retention, _sync, _workStart, _workEnd, _timeZone, _inviteSend,
                     _inviteTimeout, _attachDir, _logLevel, _tree, _allowed, _picker })
            _errors.SetError(c, "");
        _errors.SetError(_status, "");
        foreach (var g in errors.GroupBy(ControlFor))
        {
            var msg = string.Join("\n", g.Select(e => e.Message));
            if (g.Key == _status) _status.Text = msg;
            else _errors.SetError(g.Key, msg);
        }
    }

    private void RefreshErrors()
    {
        try { ShowErrors(_editor.Errors()); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private void RouteToFirst(IReadOnlyList<SettingsError> errors)
    {
        ShowErrors(errors);
        var first = errors[0];
        SelectTab(SettingsEditor.TabFor(first.Field));
        if (SettingsEditor.SharedMailboxIndexFor(first.Field) is { } i && i < _editor.Draft.SharedMailboxes.Count)
        {
            var key = EmailAddress.Normalize(_editor.Draft.SharedMailboxes[i].Smtp);
            var idx = _editor.MailboxChoices().ToList().FindIndex(c => c.Key == key);
            if (idx >= 0) _picker.SelectedIndex = idx;
        }
        _status.Text = first.Message;
    }

    private async Task ApplyAsync(bool close)
    {
        if (_busy) return;
        try
        {
            var errors = _editor.Errors();
            if (errors.Count > 0)
            {
                RouteToFirst(errors);
                return;
            }
            var live = _s.GetSettings();
            var changes = _editor.Changes(live);
            if (changes.PortChanged)
            {
                var answer = MessageBox.Show(this, SettingsTexts.PortChangeQuestion(live.Port, _editor.Draft.Port), BalloonTexts.Title,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.OK) return;
            }
            _busy = true;
            _ok.Enabled = _cancel.Enabled = _apply.Enabled = false;
            _status.Text = "Applying…";
            var result = await _s.Applier.ApplyAsync(_editor, _cts.Token);
            if (IsDisposed) return;
            if (!result.Ok)
            {
                RouteToFirst(result.Errors);
                return;
            }
            _editor = new SettingsEditor(_s.GetSettings());
            BindAll(_key);
            _status.Text = result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : "Saved.";
            if (close) Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _status.Text = "Couldn't apply: " + ex.Message;
        }
        finally
        {
            _busy = false;
            if (!IsDisposed) _ok.Enabled = _cancel.Enabled = _apply.Enabled = true;
        }
    }
}
