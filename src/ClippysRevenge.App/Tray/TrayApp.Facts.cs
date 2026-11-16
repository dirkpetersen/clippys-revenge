using System.ComponentModel;
using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>"Edit my facts…" in the AI drafts submenu (SPEC 25.2). UI thread only.</summary>
internal sealed partial class TrayApp
{
    private readonly ToolStripMenuItem _aiEditFacts = new(AiMenuTexts.EditFacts);
    private int _editFactsBusy;   // Interlocked; a click while one runs is ignored

    /// <summary>Wires the Click handler and appends the item after "AI drafts settings…".</summary>
    private void BuildEditFactsItem()
    {
        _aiEditFacts.Click += (_, _) => EditFactsClicked();
        _ai.DropDownItems.Add(_aiEditFacts);
    }

    private void ApplyEditFactsItem(AiDraftsMenuModel m) =>
        _aiEditFacts.Enabled = m.EditFactsEnabled && _s.Ai?.Facts is not null;

    /// <summary>async void; never throws. Creates the template when the file is missing, then opens it in Notepad.</summary>
    private async void EditFactsClicked()
    {
        var facts = _s.Ai?.Facts;
        if (facts is null || Interlocked.CompareExchange(ref _editFactsBusy, 1, 0) != 0)
        {
            return;
        }
        try
        {
            var target = await facts.PrepareAsync(CancellationToken.None);
            if (target.Path is null)
            {
                _s.Balloons.PostUserFeedback(AiTabTexts.OpenFailed(target.Error ?? "something went wrong"));
                return;
            }
            FactsNotepad.Open(target.Path);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _s.Balloons.PostUserFeedback(AiTabTexts.OpenFailed(ex.Message));
        }
        catch (Exception ex)
        {
            _s.Balloons.PostUserFeedback(AiTabTexts.OpenFailed("something went wrong (" + ex.GetType().Name + ")"));
        }
        finally
        {
            Interlocked.Exchange(ref _editFactsBusy, 0);
        }
    }
}
