using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>
/// Activity window (SPEC 6.4): current state, a backfill progress bar and the last 500 events with
/// kind and mailbox filters and a Copy button. Shows only what <see cref="ActivityHub"/> recorded.
/// </summary>
internal sealed class ActivityForm : Form
{
    private const string AllKinds = "All kinds";
    private const string AllMailboxes = "All mailboxes";

    private readonly ActivityHub _hub;
    private readonly Label _state = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Visible = false, Minimum = 0, Maximum = 100 };
    private readonly Label _barText = new() { AutoSize = true, Visible = false };
    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _mailbox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Button _copy = new() { Text = "Copy", AutoSize = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false,
        GridLines = true,
    };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 200 };
    private readonly Font _bold;
    private int _refreshQueued;
    private bool _refreshing;
    private List<ActivityRow> _rows = [];

    public ActivityForm(ActivityHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = hub;
        Text = "Clippy — Activity";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 480);
        MinimumSize = new Size(560, 300);
        ShowInTaskbar = true;
        _bold = new Font(Font, FontStyle.Bold);

        _list.Columns.Add("Time", 70);
        _list.Columns.Add("Mailbox", 150);
        _list.Columns.Add("Kind", 70);
        _list.Columns.Add("Summary", 400);
        _list.Columns.Add("Duration", 80);

        _kind.Items.Add(AllKinds);
        foreach (var k in Enum.GetValues<ActivityKind>())
        {
            _kind.Items.Add(ActivityView.KindName(k));
        }
        _kind.SelectedIndex = 0;
        _mailbox.Items.Add(AllMailboxes);
        _mailbox.SelectedIndex = 0;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 10, 10, 4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_state, 0, 0);
        top.SetColumnSpan(_state, 2);
        top.Controls.Add(_bar, 0, 1);
        top.Controls.Add(_barText, 1, 1);

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        filters.Controls.AddRange([new Label { Text = "Kind:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) }, _kind,
            new Label { Text = "Mailbox:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }, _mailbox, _copy]);
        _copy.Margin = new Padding(12, 0, 0, 0);

        Controls.Add(_list);
        Controls.Add(filters);
        Controls.Add(top);

        _kind.SelectedIndexChanged += (_, _) => Refill();
        _mailbox.SelectedIndexChanged += (_, _) => Refill();
        _copy.Click += (_, _) => CopyRows();
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refill();
        };

        _hub.Changed += OnHubChanged;
        Disposed += (_, _) =>
        {
            _hub.Changed -= OnHubChanged;
            _refreshTimer.Dispose();
            _bold.Dispose();
        };
        Refill();
    }

    // Hub.Changed can fire on any thread: only marshal to the UI thread and coalesce.
    private void OnHubChanged()
    {
        if (IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed)
                {
                    _refreshTimer.Stop();
                    _refreshTimer.Start();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Window is closing.
        }
    }

    private ActivityKind? SelectedKind => _kind.SelectedIndex <= 0 ? null : Enum.GetValues<ActivityKind>()[_kind.SelectedIndex - 1];

    private string? SelectedMailbox => _mailbox.SelectedIndex <= 0 ? null : _mailbox.SelectedItem as string;

    private void Refill()
    {
        if (_refreshing)
        {
            return;
        }
        _refreshing = true;
        try
        {
            var header = ActivityView.Header(_hub.State, _hub.Progress);
            _state.Text = header.StateText;
            _bar.Visible = _barText.Visible = header.ShowProgress;
            _bar.Value = Math.Clamp(header.ProgressPercent, 0, 100);
            _barText.Text = header.ProgressText;

            var events = _hub.GetEvents();
            var keep = SelectedMailbox;
            var boxes = ActivityView.Mailboxes(events);
            var have = _mailbox.Items.Cast<string>().Skip(1).ToList();
            if (!have.SequenceEqual(boxes, StringComparer.OrdinalIgnoreCase))
            {
                _mailbox.Items.Clear();
                _mailbox.Items.Add(AllMailboxes);
                foreach (var b in boxes)
                {
                    _mailbox.Items.Add(b);
                }
                var idx = keep is null ? 0 : _mailbox.Items.IndexOf(keep);
                _mailbox.SelectedIndex = idx < 0 ? 0 : idx;
            }

            _rows = ActivityView.Filter(events, SelectedKind, SelectedMailbox).Select(e => ActivityView.Row(e)).ToList();
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var r in _rows)
                {
                    var item = new ListViewItem([r.Time, r.Mailbox, r.Kind, r.Summary, r.Duration]);
                    if (r.Bold)
                    {
                        item.Font = _bold;
                    }
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void CopyRows()
    {
        var selected = _list.SelectedIndices.Cast<int>().Where(i => i < _rows.Count).Select(i => _rows[i]).ToList();
        var text = ActivityView.CopyText(selected.Count > 0 ? selected : _rows);
        if (text.Length == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            MessageBox.Show(this, "Couldn't reach the clipboard — try again.", BalloonTexts.Title);
        }
    }
}
