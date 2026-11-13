namespace ClippysRevenge.App.Tray;

/// <summary>A read-only text window (Settings → AI drafts → View).</summary>
internal sealed class TextViewForm : Form
{
    public TextViewForm(string title, string text)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(360, 240);
        ShowInTaskbar = false;

        var close = new Button { Text = "Close", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.Cancel };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        Controls.Add(box);
        Controls.Add(close);
        CancelButton = close;
        box.Select(0, 0);
    }
}
