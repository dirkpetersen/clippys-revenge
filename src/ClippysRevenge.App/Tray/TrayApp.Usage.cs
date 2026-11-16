namespace ClippysRevenge.App.Tray;

// The disabled "Today: ..." line at the top of the AI drafts submenu (SPEC 6.2, 19.13).
internal sealed partial class TrayApp
{
    private readonly ToolStripMenuItem _aiUsage = new() { Enabled = false, Visible = false };

    // Runs first in BuildAiMenu, so it is the first line of the submenu.
    private void BuildUsageItem() => _ai.DropDownItems.Add(_aiUsage);

    private void ApplyUsageItem()
    {
        var text = SafeUsageLine();
        _aiUsage.Visible = text is not null;
        _aiUsage.Text = text ?? "";
    }

    private string? SafeUsageLine()
    {
        try
        {
            return _s.AiUsageLine?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
