using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>The "Rescan emails from the last 24 hours" item (SPEC 23.8). UI thread only.</summary>
internal sealed partial class TrayApp
{
    private readonly ToolStripMenuItem _aiRescan = new(RescanTexts.MenuItem);
    private int _rescanPhase;   // 0 idle, 1 the tray is preparing or awaiting a rescan (Interlocked)

    /// <summary>The rescan item. async void; never throws.</summary>
    private async void RescanClicked()
    {
        var ai = _s.Ai;
        if (ai is null)
        {
            return;
        }
        if (ai.CurrentRescan() is not null)
        {
            ai.CancelRescan();
            return;
        }
        if (Interlocked.CompareExchange(ref _rescanPhase, 1, 0) != 0)
        {
            return;
        }
        try
        {
            if (!_s.GetSettings().AiDrafting.HasConsent)
            {
                if (!AiConsentDialog.Ask(null))
                {
                    return;
                }
                ai.Toggler.RecordConsent();
            }
            var status = await ai.Checker.CheckAsync(refresh: false, CancellationToken.None);
            if (!status.Ready)
            {
                _s.Balloons.PostUserFeedback(RescanTexts.NotReady(status.Text));
                return;
            }
            var plan = await Task.Run(ai.PlanRescan);
            var text = RescanFlow.PlanBalloon(plan);
            if (text is not null)
            {
                _s.Balloons.PostUserFeedback(text);
                return;
            }
            if (_disposed || !ConfirmRescan(plan))
            {
                return;
            }
            var r = await ai.Rescan(CancellationToken.None);
            var b = RescanFlow.ResultBalloon(r);
            if (b is not null)
            {
                _s.Balloons.PostUserFeedback(b.Text, b.Action);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _s.Balloons.PostUserFeedback(RescanTexts.StartFailed(ex.Message));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Type only: the message could carry text we don't want in a balloon.
            _s.Balloons.PostUserFeedback(RescanTexts.StartFailed("something went wrong (" + ex.GetType().Name + ")"));
        }
        finally
        {
            Interlocked.Exchange(ref _rescanPhase, 0);
        }
    }

    /// <summary>The confirmation task dialog. True only for the Rescan button.</summary>
    private static bool ConfirmRescan(RescanPlan plan)
    {
        var rescan = new TaskDialogButton(RescanTexts.ConfirmButton);
        var cancel = new TaskDialogButton(RescanTexts.CancelButton);
        var page = new TaskDialogPage
        {
            Caption = BalloonTexts.Title,
            Heading = RescanTexts.ConfirmTitle,
            Text = plan.ConfirmText,
            AllowCancel = true,
            DefaultButton = cancel,
        };
        page.Buttons.Add(rescan);
        page.Buttons.Add(cancel);
        return TaskDialog.ShowDialog(page) == rescan;
    }
}
