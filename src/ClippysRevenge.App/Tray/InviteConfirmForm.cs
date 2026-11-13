using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>
/// The user's confirmation step before an invite is sent (SPEC 6.6). Topmost without
/// taking focus; Enter and Esc both mean Cancel.
/// </summary>
internal sealed class InviteConfirmForm : Form
{
    private const int WsExTopmost = 0x8;

    private readonly DateTimeOffset _deadline;
    private readonly TimeProvider _time;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Label _countdown = new() { AutoSize = true };
    private readonly Button _send = new() { Text = InviteConfirmTexts.SendButton, AutoSize = true };
    private readonly Button _saveDraft = new() { Text = InviteConfirmTexts.SaveDraftButton, AutoSize = true };
    private readonly Button _cancel = new() { Text = InviteConfirmTexts.CancelButton, AutoSize = true };

    public InviteConfirmForm(InviteConfirmView view, DateTimeOffset deadline, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        _deadline = deadline;
        _time = timeProvider ?? TimeProvider.System;

        Text = view.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(520, 560);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));

        var bold = new Font(Font, FontStyle.Bold);
        Disposed += (_, _) => bold.Dispose();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 0,
            Padding = new Padding(10),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label Line(string text, Font? font = null) => new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(490, 0),
            Font = font ?? Font,
            Margin = new Padding(0, 2, 0, 2),
        };

        void Add(Control c, bool fill = false)
        {
            layout.RowCount++;
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c, 0, layout.RowCount - 1);
        }

        Add(Line(view.Heading));
        Add(Line(view.OrganizerLine));
        Add(Line(view.Subject, bold));
        if (view.WhenShort is not null)
        {
            Add(Line(view.WhenShort));
        }
        Add(Line(view.WhenFull));
        Add(Line(view.LocationLine));
        Add(Line(view.TeamsLine));
        Add(Line(view.RecipientsHeader));

        var list = new ListView
        {
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            MultiSelect = false,
            Dock = DockStyle.Fill,
            Height = 90,
            Margin = new Padding(0, 2, 0, 2),
        };
        list.Columns.Add("", 470);
        foreach (var r in view.Recipients)
        {
            var item = new ListViewItem(r.Text);
            if (r.External)
            {
                item.Font = bold;
                item.BackColor = Color.FromArgb(255, 236, 179);
            }
            list.Items.Add(item);
        }
        Add(list);

        if (view.ExternalSummary is not null)
        {
            Add(Line(view.ExternalSummary, bold));
        }

        Add(new TextBox
        {
            Text = view.Body,
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
        }, fill: true);

        Add(_countdown);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
        };
        buttons.Controls.AddRange([_send, _saveDraft, _cancel]);
        Add(buttons);

        Controls.Add(layout);

        _send.Click += (_, _) => Choose(InviteChoice.Send);
        _saveDraft.Click += (_, _) => Choose(InviteChoice.SaveDraft);
        _cancel.Click += (_, _) => Choose(InviteChoice.Cancel);
        AcceptButton = _cancel;
        CancelButton = _cancel;
        ActiveControl = _cancel;

        _timer.Tick += (_, _) => UpdateCountdown();
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
        };
        UpdateCountdown();
        _timer.Start();
    }

    /// <summary>Set by a button click, else null.</summary>
    public InviteChoice? Choice { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTopmost;
            return cp;
        }
    }

    private void Choose(InviteChoice choice)
    {
        Choice = choice;
        Close();
    }

    private void UpdateCountdown()
    {
        var remaining = _deadline - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _countdown.Text = InviteConfirmTexts.SavingNow;
            _send.Enabled = false;
            _saveDraft.Enabled = false;
        }
        else
        {
            _countdown.Text = InviteConfirmTexts.Countdown(remaining);
        }
    }
}
