using System.ComponentModel;
using System.Runtime.InteropServices;
using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>
/// About window (SPEC 6.7): version, runtime, location, update mode, last check and status, the docs and release
/// notes links, and the Update now / Check for updates buttons. All decisions come from <see cref="AboutView"/>.
/// </summary>
internal sealed class AboutForm : Form
{
    private readonly UpdateService _updates;
    private readonly Action<string> _openLink;
    private readonly Label _version = Line(), _runtime = Line(), _location = Line(), _mode = Line(), _lastCheck = Line(), _status = Line();
    private readonly LinkLabel _docs = new() { AutoSize = true };
    private readonly LinkLabel _notes = new() { AutoSize = true };
    private readonly Button _update = new() { Text = UpdateTexts.UpdateNow, Width = 140, Height = 28 };
    private readonly Button _check = new() { Text = UpdateTexts.CheckButton, Width = 140, Height = 28 };
    private readonly Button _close = new() { Text = UpdateTexts.CloseButton, Width = 90, Height = 28, DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    private readonly Font _bold;
    private AboutModel? _model;

    /// <param name="openLink">TrayApp.ShellOpen.</param>
    /// <param name="icon">The Idle tray icon (not disposed by the form).</param>
    public AboutForm(UpdateService updates, Action<string> openLink, Icon? icon)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(openLink);
        _updates = updates;
        _openLink = openLink;

        Text = UpdateTexts.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        ClientSize = new Size(480, 360);
        CancelButton = _close;
        if (icon is not null)
        {
            Icon = icon;
        }

        _bold = new Font(Font, FontStyle.Bold);
        var y = 14;
        if (icon is not null)
        {
            var pic = new PictureBox { Image = new Icon(icon, new Size(32, 32)).ToBitmap(), SizeMode = PictureBoxSizeMode.CenterImage };
            pic.SetBounds(16, y, 36, 36);
            Controls.Add(pic);
        }
        var name = new Label { Text = UpdateTexts.ProductName, Font = _bold, AutoSize = false };
        name.SetBounds(60, y + 8, 400, 22);
        Controls.Add(name);
        y += 50;
        foreach (var l in new[] { _version, _runtime, _location, _mode, _lastCheck, _status })
        {
            l.SetBounds(16, y, 448, 36);
            Controls.Add(l);
            y += 38;
        }
        _docs.SetBounds(16, y + 2, 120, 20);
        _notes.SetBounds(150, y + 2, 120, 20);
        Controls.Add(_docs);
        Controls.Add(_notes);
        y += 34;
        _update.Location = new Point(16, y);
        _check.Location = new Point(166, y);
        _close.Location = new Point(374, y);
        Controls.AddRange([_update, _check, _close]);
        ClientSize = new Size(480, y + 44);

        _docs.LinkClicked += (_, _) => Open(_model?.DocsUrl);
        _notes.LinkClicked += (_, _) => Open(_model?.NotesUrl);
        _update.Click += (_, _) => _ = _updates.UpdateNowAsync(CancellationToken.None);
        _check.Click += (_, _) => StartCheck();
        _close.Click += (_, _) => Close();

        Load += (_, _) => Bind();
        _timer.Tick += (_, _) => Bind();
        _timer.Start();
        _updates.Changed += OnChanged;
        FormClosed += (_, _) => _updates.Changed -= OnChanged;
    }

    /// <summary>UI thread. Starts UpdateService.CheckNowAsync (fire and forget; the service never throws).</summary>
    public void StartCheck() => _ = _updates.CheckNowAsync(CancellationToken.None);

    private static Label Line() => new() { AutoSize = false };

    private void OnChanged()
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(Bind);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing; nothing to update.
        }
    }

    private void Bind()
    {
        if (IsDisposed)
        {
            return;
        }
        var m = AboutView.Build(_updates.Status, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        _model = m;
        Text = m.Title;
        _version.Text = m.VersionLine;
        _runtime.Text = m.RuntimeLine;
        _location.Text = m.LocationLine;
        _mode.Text = m.ModeLine;
        _lastCheck.Text = m.LastCheckLine;
        _status.Text = m.StatusLine;
        _docs.Text = UpdateTexts.DocsLink;
        _notes.Text = UpdateTexts.NotesLink;
        _update.Text = m.UpdateNowText;
        _update.Enabled = m.UpdateNowEnabled;
        _check.Enabled = m.CheckEnabled;
    }

    private void Open(string? url)
    {
        if (url is null)
        {
            return;
        }
        try
        {
            _openLink(url);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No browser: show nothing.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updates.Changed -= OnChanged;
            _timer.Stop();
            _timer.Dispose();
            _bold.Dispose();
        }
        base.Dispose(disposing);
    }
}
