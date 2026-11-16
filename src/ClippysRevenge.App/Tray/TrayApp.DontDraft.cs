using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>The "Don't draft for this sender" items of the AI drafts submenu (SPEC 27.7). UI thread only.</summary>
internal sealed partial class TrayApp
{
    private readonly ToolStripMenuItem _aiDontDraftSender = new(AiMenuTexts.DontDraftSender);
    private readonly ToolStripMenuItem _aiDontDraftDomain = new(AiMenuTexts.DontDraftDomain);
    private int _dontDraftBusy;   // Interlocked; a click while one runs is ignored

    /// <summary>Wires the Click handlers and inserts both items right after "Draft a reply to the selected email".</summary>
    private void BuildDontDraftItems()
    {
        _aiDontDraftSender.Click += (_, _) => DontDraftClicked(DontDraftKind.Sender);
        _aiDontDraftDomain.Click += (_, _) => DontDraftClicked(DontDraftKind.Domain);
        var at = _ai.DropDownItems.IndexOf(_aiDraftSelected) + 1;
        _ai.DropDownItems.Insert(at, _aiDontDraftSender);
        _ai.DropDownItems.Insert(at + 1, _aiDontDraftDomain);
    }

    private void ApplyDontDraftItems(AiDraftsMenuModel m)
    {
        var enabled = m.DontDraftEnabled && _s.Ai?.DontDraft is not null;
        _aiDontDraftSender.Enabled = enabled;
        _aiDontDraftDomain.Enabled = enabled;
    }

    /// <summary>async void; never throws.</summary>
    private async void DontDraftClicked(DontDraftKind kind)
    {
        var svc = _s.Ai?.DontDraft;
        if (svc is null || Interlocked.CompareExchange(ref _dontDraftBusy, 1, 0) != 0)
        {
            return;
        }
        try
        {
            var prepared = await svc.PrepareAsync(kind, CancellationToken.None);
            if (prepared.Balloon is not null)
            {
                _s.Balloons.PostUserFeedback(prepared.Balloon);
                return;
            }
            if (prepared.NeedsConfirm && (_disposed || !ConfirmDomain(prepared.Entry!)))
            {
                return;
            }
            var text = await Task.Run(() => svc.Apply(prepared));
            _s.Balloons.PostUserFeedback(text);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Type only: the message could carry the address.
            _s.Balloons.PostUserFeedback(AiMenuTexts.ToggleFailed("something went wrong (" + ex.GetType().Name + ")"));
        }
        finally
        {
            Interlocked.Exchange(ref _dontDraftBusy, 0);
        }
    }

    /// <summary>The confirmation task dialog. True only for Add.</summary>
    private static bool ConfirmDomain(string domain)
    {
        var add = new TaskDialogButton(DontDraftTexts.AddButton);
        var cancel = new TaskDialogButton(DontDraftTexts.CancelButton);
        var page = new TaskDialogPage
        {
            Caption = BalloonTexts.Title,
            Heading = DontDraftTexts.ConfirmHeading(domain),
            AllowCancel = true,
            DefaultButton = cancel,
        };
        page.Buttons.Add(add);
        page.Buttons.Add(cancel);
        return TaskDialog.ShowDialog(page) == add;
    }
}
