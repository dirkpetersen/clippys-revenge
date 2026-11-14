using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>What the AI drafts tab needs from the app (SPEC 19.10). Built by Program only.</summary>
internal sealed record AiSettingsServices(
    AiDraftingToggler Toggler,
    Func<bool> HasApiKey,                 // () => store.LoadApiKey() is not null
    Action<string?> SaveApiKey,           // store.SaveApiKey: Program's single SettingsStore
    Action<string> ShellOpen,             // TrayApp.ShellOpen
    AiStatusChecker? Checker = null,      // null when the index failed to open
    StyleSkill? Style = null,
    Func<CancellationToken, Task<Recipient?>>? Owner = null,
    Func<AiDraftsToday>? Today = null);

/// <summary>The AI drafts tab (SPEC 19.10). Edits the staged draft; consent and API key changes apply at once.</summary>
internal sealed partial class SettingsForm
{
    private readonly CheckBox _aiEnabled = new() { Text = AiTabTexts.EnableText };
    private readonly RadioButton _aiUseCli = new() { Text = AiTabTexts.UseClaudeCode };
    private readonly RadioButton _aiUseKey = new() { Text = AiTabTexts.UseApiKey };
    private readonly Label _aiStatus = new() { AutoEllipsis = true };
    private readonly Button _aiCheck = new() { Text = "Check" };
    private readonly Label _aiCheckNote = new() { ForeColor = SystemColors.GrayText };
    private readonly Label _aiHint = new() { ForeColor = SystemColors.GrayText };
    private readonly TextBox _apiKeyBox = new() { UseSystemPasswordChar = true };
    private readonly Button _aiKeySave = new() { Text = "Save key", Enabled = false };
    private readonly Button _aiKeyRemove = new() { Text = "Remove key…", Enabled = false };
    private readonly Label _aiKeyState = new();
    private readonly TextBox _aiCliPath = new();
    private readonly Button _aiCliBrowse = new() { Text = "Browse…" };
    private readonly TextBox _aiModel = new();
    private readonly ComboBox _aiEffort = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _aiCap = new(), _aiMinConf = new(), _aiMaxAge = new();
    private readonly CheckBox _aiMeeting = new() { Text = AiTabTexts.MeetingTimesText };
    private readonly TextBox _aiMeetingCount = new();
    private readonly CheckBox _aiDirect =new() { Text = "Only when I'm in To or CC" };
    private readonly TextBox _aiExclude = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly TextBox _aiInclude = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly CheckBox _aiOwnBox = new() { Text = SettingsEditor.OwnMailboxLabel, Enabled = false };
    private readonly CheckedListBox _aiMailboxes = new() { CheckOnClick = true };
    private readonly Label _aiStyleStatus = new() { AutoEllipsis = true };
    private readonly CheckBox _aiWeekly = new() { Text = "Update weekly" };
    private readonly Button _aiView = new() { Text = "View" };
    private readonly Button _aiRegen = new() { Text = "Regenerate…" };
    private readonly Button _aiOpen = new() { Text = "Open in editor" };
    private readonly Label _aiStyleNote = new() { ForeColor = SystemColors.GrayText };
    private readonly Label _aiToday = new();
    private readonly LinkLabel _aiManual = new() { Text = AiTabTexts.ManualLinkText };

    private IReadOnlyList<AiMailboxChoice> _aiChoices = [];
    private TextViewForm? _aiTextView;
    private bool _aiChecked, _aiChecking, _aiBusyRegen;
    private int _aiTodayGen;

    private AiDraftingSettings AiDraft => _editor.Draft.AiDrafting;

    private AiDraftingSettings AiLive => _s.GetSettings().AiDrafting;

    private void BuildAi()
    {
        var p = NewPage(AiTabTexts.TabTitle);
        p.AutoScroll = true;

        Put(p, _aiEnabled, 8, 8, 620, 24);
        Lbl(p, AiTabTexts.UseLabel, 8, 38, 44);
        Put(p, _aiUseCli, 56, 38, 560, 24);
        Put(p, _aiUseKey, 56, 62, 560, 24);
        Put(p, _aiStatus, 8, 94, 520, 22);
        Put(p, _aiCheck, 536, 91, 80, 26);
        Put(p, _aiCheckNote, 8, 118, 620, 18);
        Put(p, _aiHint, 8, 138, 620, 34);

        Lbl(p, "API key", 8, 178, 80);
        Put(p, _apiKeyBox, 96, 178, 260, 24);
        Put(p, _aiKeySave, 362, 177, 80, 26);
        Put(p, _aiKeyRemove, 448, 177, 100, 26);
        Put(p, _aiKeyState, 96, 208, 520, 20);

        Lbl(p, "Claude Code path (optional)", 8, 236, 240);
        Put(p, _aiCliPath, 8, 262, 500, 24);
        Put(p, _aiCliBrowse, 516, 261, 80, 26);

        Lbl(p, "Model", 8, 298, 120);
        Put(p, _aiModel, 140, 298, 200, 24);
        Lbl(p, "Effort", 360, 298, 60);
        Put(p, _aiEffort, 424, 298, 100, 24);

        Lbl(p, "Daily limit (Claude calls)", 8, 330, 170);
        Put(p, _aiCap, 180, 330, 60, 24);
        Lbl(p, "Minimum confidence (0–1)", 270, 330, 170);
        Put(p, _aiMinConf, 444, 330, 60, 24);

        Lbl(p, "Ignore mail older than (days)", 8, 362, 170);
        Put(p, _aiMaxAge, 180, 362, 60, 24);
        Put(p, _aiDirect, 270, 362, 300, 24);

        Put(p, _aiMeeting, 8, 394, 370, 24);
        Lbl(p, AiTabTexts.MeetingCountLabel, 384, 398, 150);
        Put(p, _aiMeetingCount, 540, 394, 60, 24);

        Lbl(p, "Never draft for", 8, 430, 200);
        Lbl(p, "Only draft for", 330, 430, 200);
        Put(p, _aiExclude, 8, 456, 310, 80);
        Put(p, _aiInclude, 330, 456, 300, 80);
        var listHint = Lbl(p, AiTabTexts.ListHint, 8, 538, 400);
        listHint.ForeColor = SystemColors.GrayText;

        Lbl(p, "Mailboxes", 8, 566, 120);
        Put(p, _aiOwnBox, 8, 592, 300, 22);
        Put(p, _aiMailboxes, 8, 618, 622, 70);

        Lbl(p, "Writing style", 8, 696, 100);
        Put(p, _aiStyleStatus, 110, 700, 520, 20);
        Put(p, _aiWeekly, 8, 724, 200, 24);
        Put(p, _aiView, 8, 754, 70, 26);
        Put(p, _aiRegen, 84, 754, 110, 26);
        Put(p, _aiOpen, 200, 754, 120, 26);
        Put(p, _aiStyleNote, 8, 786, 620, 34);

        Put(p, _aiToday, 8, 828, 620, 20);
        Put(p, _aiManual, 8, 852, 620, 20);
        _tabs.TabPages.Add(p);

        foreach (var e in AiDraftingSettings.Efforts) _aiEffort.Items.Add(e);
        var linkStart = AiTabTexts.ManualLinkText.IndexOf("AI drafts in the manual", StringComparison.Ordinal);
        if (linkStart >= 0) _aiManual.Links.Add(linkStart, "AI drafts in the manual".Length);

        // Staged edits.
        _aiEnabled.CheckedChanged += (_, _) => AiEnabledChanged();
        _aiUseCli.CheckedChanged += (_, _) => AiBackendChanged(_aiUseCli, AiBackend.ClaudeCode);
        _aiUseKey.CheckedChanged += (_, _) => AiBackendChanged(_aiUseKey, AiBackend.ApiKey);
        _aiCliPath.TextChanged += (_, _) =>
        {
            if (_binding) return;
            AiDraft.ClaudeCliPath = _aiCliPath.Text;
            RefreshAiCheckNote();
        };
        _aiModel.TextChanged += (_, _) =>
        {
            if (_binding) return;
            AiDraft.Model = _aiModel.Text;
            RefreshAiCheckNote();
        };
        _aiEffort.SelectedIndexChanged += (_, _) =>
        {
            if (_binding || _aiEffort.SelectedIndex < 0) return;
            AiDraft.Effort = AiDraftingSettings.Efforts[_aiEffort.SelectedIndex];
            RefreshErrors();
        };
        _aiCap.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _editor.TrySetInt("AiDrafting.DailyCap", _aiCap.Text, v => AiDraft.DailyCap = v);
        };
        _aiMaxAge.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _editor.TrySetInt("AiDrafting.MaxAgeDays", _aiMaxAge.Text, v => AiDraft.MaxAgeDays = v);
        };
        _aiMinConf.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _editor.TrySetDouble("AiDrafting.MinConfidence", _aiMinConf.Text, v => AiDraft.MinConfidence = v);
        };
        _aiMeeting.CheckedChanged += (_, _) =>
        {
            _aiMeetingCount.Enabled = _aiMeeting.Checked;
            if (!_binding) AiDraft.MeetingTimes = _aiMeeting.Checked;
        };
        _aiMeetingCount.TextChanged += (_, _) =>
        {
            if (_binding) return;
            _editor.TrySetInt("AiDrafting.MeetingTimeOptions", _aiMeetingCount.Text, v => AiDraft.MeetingTimeOptions = v);
        };
        _aiDirect.CheckedChanged += (_, _) => { if (!_binding) AiDraft.RequireDirectRecipient = _aiDirect.Checked; };
        _aiWeekly.CheckedChanged += (_, _) => { if (!_binding) AiDraft.AutoRegenerateStyle = _aiWeekly.Checked; };
        _aiExclude.TextChanged += (_, _) => { if (!_binding) AiDraft.ExcludeSenders = AiSettingsLines.Parse(_aiExclude.Text); };
        _aiInclude.TextChanged += (_, _) => { if (!_binding) AiDraft.IncludeOnlySenders = AiSettingsLines.Parse(_aiInclude.Text); };
        foreach (var t in new[] { _aiModel, _aiCliPath, _aiCap, _aiMinConf, _aiMaxAge, _aiMeetingCount, _aiExclude, _aiInclude })
            t.Validated += (_, _) => RefreshErrors();
        _aiMailboxes.ItemCheck += (_, e) =>
        {
            if (_binding || e.Index < 0 || e.Index >= _aiChoices.Count) return;
            _editor.SetAiDrafts(_aiChoices[e.Index].Key, e.NewValue == CheckState.Checked);
        };
        _aiCliBrowse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog
            {
                Filter = "Claude Code (claude.exe;cli.js)|claude.exe;cli.js|Programs (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (d.ShowDialog(this) == DialogResult.OK) _aiCliPath.Text = d.FileName;
        };

        // Check and API key.
        _aiCheck.Click += async (_, _) => await RunAiCheckAsync(refresh: true);
        _apiKeyBox.TextChanged += (_, _) => _aiKeySave.Enabled = _apiKeyBox.TextLength > 0;
        _aiKeySave.Click += async (_, _) => await ChangeApiKeyAsync(remove: false);
        _aiKeyRemove.Click += async (_, _) =>
        {
            var answer = MessageBox.Show(this, AiTabTexts.RemoveKeyQuestion, BalloonTexts.Title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.OK) await ChangeApiKeyAsync(remove: true);
        };

        // Writing style.
        _aiView.Click += (_, _) => ViewStyle();
        _aiRegen.Click += (_, _) => RegenerateStyle();
        _aiOpen.Click += (_, _) => OpenStyleInEditor();
        _aiManual.LinkClicked += (_, _) =>
        {
            try
            {
                _s.Ai.ShellOpen(AiTabTexts.ManualUrl);
            }
            catch (Exception ex)
            {
                _status.Text = AiTabTexts.OpenFailed(ex.Message);
            }
        };

        if (_s.Ai.Style is null)
        {
            foreach (var c in new Control[] { _aiWeekly, _aiView, _aiRegen, _aiOpen }) c.Enabled = false;
            _aiStyleNote.Text = AiTabTexts.NoIndex;
        }
        _aiToday.Visible = _s.Ai.Today is not null;
    }

    /// <summary>Called from BindAll (under the form's binding guard): shows the draft in the tab.</summary>
    private void BindAi()
    {
        var prev = _binding;
        _binding = true;
        try
        {
            var d = AiDraft;
            _aiEnabled.Checked = d.Enabled;
            _aiUseCli.Checked = d.Backend == AiBackend.ClaudeCode;
            _aiUseKey.Checked = d.Backend == AiBackend.ApiKey;
            _aiCliPath.Text = d.ClaudeCliPath;
            _aiModel.Text = d.Model;
            _aiEffort.SelectedIndex = AiDraftingSettings.Efforts.ToList().IndexOf(d.Effort);
            _aiCap.Text = d.DailyCap.ToString(CultureInfo.InvariantCulture);
            _aiMinConf.Text = d.MinConfidence.ToString("0.##", CultureInfo.InvariantCulture);
            _aiMaxAge.Text = d.MaxAgeDays.ToString(CultureInfo.InvariantCulture);
            _aiDirect.Checked = d.RequireDirectRecipient;
            _aiMeeting.Checked = d.MeetingTimes;
            _aiMeetingCount.Enabled = d.MeetingTimes;
            _aiMeetingCount.Text = d.MeetingTimeOptions.ToString(CultureInfo.InvariantCulture);
            _aiWeekly.Checked = d.AutoRegenerateStyle;
            _aiExclude.Text = AiSettingsLines.Join(d.ExcludeSenders);
            _aiInclude.Text = AiSettingsLines.Join(d.IncludeOnlySenders);
            _aiOwnBox.Checked = d.Enabled;
            _apiKeyBox.Text = "";
            FillAiMailboxes();
        }
        finally
        {
            _binding = prev;
        }
        RefreshAiCheckNote();
    }

    private void FillAiMailboxes()
    {
        var prev = _binding;
        _binding = true;
        try
        {
            _aiChoices = _editor.AiMailboxChoices();
            _aiMailboxes.Items.Clear();
            foreach (var c in _aiChoices) _aiMailboxes.Items.Add(c.Label, c.AiDrafts);
        }
        finally
        {
            _binding = prev;
        }
    }

    private void RefreshAiCheckNote() => _aiCheckNote.Text = AiTabTexts.PendingCheckNote(AiDraft, AiLive);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_tabs.SelectedIndex == (int)SettingsTab.AiDrafts) OnAiTabSelected();
    }

    /// <summary>Every time the AI tab is selected: fresh lists, status, style and the Today line. The first time also checks.</summary>
    private void OnAiTabSelected()
    {
        FillAiMailboxes();
        RefreshStyleStatus();
        RefreshAiToday();
        if (_aiChecked) return;
        _aiChecked = true;
        _ = RunAiCheckAsync(refresh: false);
        _ = RefreshKeyStateAsync();
    }

    // ---- enable, backend ----

    /// <summary>
    /// Live consent is adopted by the draft. Without any consent the dialog asks; Turn on records it at once
    /// (like the tray) and the draft adopts it. False when the user cancelled or the save failed.
    /// </summary>
    private bool EnsureAiConsent()
    {
        if (AiLive.ConsentedAtUtc is { } c)
        {
            _editor.AdoptConsent(c);
            return true;
        }
        if (!AiConsentDialog.Ask(this)) return false;
        try
        {
            _editor.AdoptConsent(_s.Ai.Toggler.RecordConsent());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = AiMenuTexts.ToggleFailed(ex.Message);
            return false;
        }
    }

    private void SetAiEnabledQuietly(bool value)
    {
        var prev = _binding;
        _binding = true;
        try { _aiEnabled.Checked = value; }
        finally { _binding = prev; }
    }

    private void AiEnabledChanged()
    {
        if (_binding) return;
        if (_aiEnabled.Checked && !EnsureAiConsent())
        {
            SetAiEnabledQuietly(false);
            return;
        }
        var d = AiDraft;
        d.SetEnabled(_aiEnabled.Checked, DateTimeOffset.UtcNow);
        _aiOwnBox.Checked = d.Enabled;
        RefreshErrors();
    }

    private void AiBackendChanged(RadioButton r, AiBackend backend)
    {
        if (_binding || !r.Checked) return;
        AiDraft.Backend = backend;
        RefreshAiCheckNote();
    }

    // ---- status check ----

    private async Task RunAiCheckAsync(bool refresh)
    {
        var checker = _s.Ai.Checker;
        if (checker is null)
        {
            _aiStatus.Text = AiTabTexts.NoIndex;
            _aiStatus.ForeColor = Color.Firebrick;
            _aiCheck.Enabled = false;
            return;
        }
        if (_aiChecking) return;
        _aiChecking = true;
        _aiCheck.Enabled = false;
        _aiStatus.Text = AiTabTexts.Checking;
        _aiStatus.ForeColor = SystemColors.ControlText;
        try
        {
            var s = await checker.CheckAsync(refresh, _cts.Token);
            if (IsDisposed) return;
            _aiStatus.Text = s.Text;
            _aiStatus.ForeColor = s.Ready ? Color.DarkGreen : Color.Firebrick;
            _aiHint.Text = !s.Ready && AiLive.Backend == AiBackend.ClaudeCode ? AiTabTexts.SignInHint : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Fixed text only: no exception message in the UI.
            if (!IsDisposed)
            {
                _aiStatus.Text = AiTabTexts.CheckFailed;
                _aiStatus.ForeColor = Color.Firebrick;
            }
        }
        finally
        {
            _aiChecking = false;
            if (!IsDisposed) _aiCheck.Enabled = true;
        }
    }

    // ---- API key (never shown, never held in a field) ----

    private async Task RefreshKeyStateAsync()
    {
        string text;
        bool enableRemove;
        try
        {
            var saved = await Task.Run(_s.Ai.HasApiKey);
            text = saved ? AiTabTexts.KeySaved : AiTabTexts.NoKeySaved;
            enableRemove = saved;
        }
        catch (Exception)
        {
            text = AiTabTexts.KeyUnreadable;
            enableRemove = true;
        }
        if (IsDisposed) return;
        _aiKeyState.Text = text;
        _aiKeyRemove.Enabled = enableRemove;
    }

    private async Task ChangeApiKeyAsync(bool remove)
    {
        string? text = null;
        if (!remove)
        {
            text = _apiKeyBox.Text;
            _apiKeyBox.Text = "";
        }
        var save = _s.Ai.SaveApiKey;
        try
        {
            await Task.Run(() => save(text));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            // SaveApiKey's own messages never contain the key (SettingsStore tests assert it).
            if (!IsDisposed) _status.Text = AiTabTexts.KeySaveFailed(ex.Message);
        }
        catch (Exception)
        {
            if (!IsDisposed) _status.Text = AiTabTexts.KeySaveFailed("something went wrong");
        }
        text = null;
        if (IsDisposed) return;
        await RefreshKeyStateAsync();
        if (!IsDisposed && AiLive.Backend == AiBackend.ApiKey) await RunAiCheckAsync(refresh: true);
    }

    // ---- writing style ----

    private async void RefreshStyleStatus()
    {
        var style = _s.Ai.Style;
        if (style is null) return;
        try
        {
            var st = await Task.Run(style.GetStatus);
            if (!IsDisposed) _aiStyleStatus.Text = st.Text(_s.TimeZone);
        }
        catch (Exception)
        {
            if (!IsDisposed) _aiStyleStatus.Text = "";
        }
    }

    private async void ViewStyle()
    {
        var style = _s.Ai.Style;
        if (style is null) return;
        try
        {
            var text = await Task.Run(style.Files.ReadSkillText);
            if (IsDisposed) return;
            if (text is null)
            {
                _aiStyleNote.Text = AiTabTexts.NoSkillYet;
                return;
            }
            if (_aiTextView is { IsDisposed: false } open)
            {
                open.Activate();
                return;
            }
            var view = new TextViewForm(AiTabTexts.ViewTitle, text) { Owner = this };
            _aiTextView = view;
            view.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_aiTextView, view)) _aiTextView = null;
                view.Dispose();
            };
            view.Show();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _aiStyleNote.Text = AiTabTexts.OpenFailed(ex.Message);
        }
    }

    private async void RegenerateStyle()
    {
        var style = _s.Ai.Style;
        if (style is null || _aiBusyRegen) return;
        _aiBusyRegen = true;
        _aiRegen.Enabled = false;
        try
        {
            if (!EnsureAiConsent()) return;
            var st = await Task.Run(style.GetStatus);
            if (IsDisposed) return;
            var replace = false;
            if (st.Kind == StyleStatusKind.Edited)
            {
                var replaceBtn = new TaskDialogButton(StyleTexts.ReplaceButton);
                var keepBtn = new TaskDialogButton(StyleTexts.KeepButton);
                var page = new TaskDialogPage
                {
                    Caption = BalloonTexts.Title,
                    Heading = StyleTexts.ReplaceQuestion,
                    AllowCancel = true,
                    DefaultButton = keepBtn,
                };
                page.Buttons.Add(replaceBtn);
                page.Buttons.Add(keepBtn);
                if (TaskDialog.ShowDialog(this, page) != replaceBtn) return;
                replace = true;
            }
            Recipient? owner = null;
            try
            {
                if (_s.Ai.Owner is { } resolve)
                    owner = await resolve(_cts.Token).WaitAsync(ComLimit, _cts.Token);
            }
            catch (TimeoutException)
            {
                owner = null;
            }
            if (IsDisposed) return;
            if (owner is null)
            {
                _aiStyleNote.Text = AiTabTexts.OwnerUnknown;
                return;
            }
            _aiStyleNote.Text = AiTabTexts.Regenerating;
            StyleRunResult result;
            try
            {
                // Not tied to the window: closing Settings doesn't abort the run, and StyleSkill records its own events.
                result = await style.RegenerateAsync(owner, replace, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new StyleRunResult.Failed(ex.GetType().Name);
            }
            if (IsDisposed) return;
            _aiStyleNote.Text = AiTabTexts.StyleResult(result);
            RefreshStyleStatus();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _aiBusyRegen = false;
            if (!IsDisposed) _aiRegen.Enabled = true;
        }
    }

    private async void OpenStyleInEditor()
    {
        var style = _s.Ai.Style;
        if (style is null) return;
        EditorTarget t;
        try
        {
            t = await Task.Run(() => StyleEditorTarget.Resolve(style.Files.Paths, File.Exists));
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _aiStyleNote.Text = AiTabTexts.OpenFailed(ex.Message);
            return;
        }
        if (IsDisposed) return;
        if (t.Path is null)
        {
            _aiStyleNote.Text = t.Message ?? AiTabTexts.NoSkillYet;
            return;
        }
        try
        {
            _s.Ai.ShellOpen(t.Path);
            _aiStyleNote.Text = "";
        }
        catch (Win32Exception)
        {
            try
            {
                _s.Ai.ShellOpen(Path.GetDirectoryName(t.Path)!);
                _aiStyleNote.Text = AiTabTexts.OpenedFolderInstead;
            }
            catch (Exception ex2)
            {
                _aiStyleNote.Text = AiTabTexts.OpenFailed(ex2.Message);
            }
        }
        catch (Exception ex)
        {
            _aiStyleNote.Text = AiTabTexts.OpenFailed(ex.Message);
        }
    }

    // ---- Today line ----

    private async void RefreshAiToday()
    {
        var today = _s.Ai.Today;
        if (today is null) return;
        var gen = ++_aiTodayGen;
        string text;
        try
        {
            var t = await Task.Run(today, _cts.Token);
            text = AiTabTexts.Today(t.Calls, AiLive.DailyCap, t.Drafts);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            text = "";
        }
        if (IsDisposed || gen != _aiTodayGen) return;
        _aiToday.Text = text;
    }

    // ---- errors ----

    private Control? AiControlFor(string field)
    {
        if (!field.StartsWith(AiDraftingSettings.FieldPrefix + ".", StringComparison.Ordinal)) return null;
        return AiTabFields.For(field) switch
        {
            AiTabField.Enabled => _aiEnabled,
            AiTabField.Backend => _aiUseCli,
            AiTabField.ClaudeCliPath => _aiCliPath,
            AiTabField.Model => _aiModel,
            AiTabField.Effort => _aiEffort,
            AiTabField.DailyCap => _aiCap,
            AiTabField.MinConfidence => _aiMinConf,
            AiTabField.MaxAgeDays => _aiMaxAge,
            AiTabField.RequireDirectRecipient => _aiDirect,
            AiTabField.MeetingTimes => _aiMeeting,
            AiTabField.MeetingTimeOptions => _aiMeetingCount,
            AiTabField.ExcludeSenders => _aiExclude,
            AiTabField.IncludeOnlySenders => _aiInclude,
            AiTabField.AutoRegenerateStyle => _aiWeekly,
            _ => _status,
        };
    }

    private Control[] AiErrorControls() =>
    [
        _aiEnabled, _aiUseCli, _aiCliPath, _aiModel, _aiEffort, _aiCap, _aiMinConf, _aiMaxAge, _aiDirect,
        _aiMeeting, _aiMeetingCount, _aiExclude, _aiInclude, _aiWeekly,
    ];
}
