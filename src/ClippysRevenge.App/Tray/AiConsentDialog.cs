using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>The AI drafts consent dialog (SPEC 19.10). Cancel is the default button and Esc.</summary>
internal static class AiConsentDialog
{
    /// <summary>UI thread. True only when the user pressed Turn on.</summary>
    public static bool Ask(IWin32Window? owner)
    {
        var turnOn = new TaskDialogButton(AiConsent.TurnOnButton);
        var cancel = new TaskDialogButton(AiConsent.CancelButton);
        var page = new TaskDialogPage
        {
            Caption = BalloonTexts.Title,
            Heading = AiConsent.Title,
            Text = AiConsent.Text,
            AllowCancel = true,
            DefaultButton = cancel,
        };
        page.Buttons.Add(turnOn);
        page.Buttons.Add(cancel);
        var choice = owner is null ? TaskDialog.ShowDialog(page) : TaskDialog.ShowDialog(owner, page);
        return choice == turnOn;
    }
}
