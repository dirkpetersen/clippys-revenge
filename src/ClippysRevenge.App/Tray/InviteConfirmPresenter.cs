using ClippysRevenge.Core;

namespace ClippysRevenge.App.Tray;

/// <summary>Shows <see cref="InviteConfirmForm"/> on the UI thread for the Core queue.</summary>
internal sealed class InviteConfirmPresenter(SynchronizationContext ui) : IInviteConfirmPresenter
{
    public Task<InviteChoice?> ShowAsync(InviteConfirmView view, DateTimeOffset deadline, CancellationToken dismiss)
    {
        var tcs = new TaskCompletionSource<InviteChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try
            {
                if (dismiss.IsCancellationRequested)
                {
                    tcs.TrySetResult(null);
                    return;
                }
                var form = new InviteConfirmForm(view, deadline);
                CancellationTokenRegistration reg = default;
                form.FormClosed += (_, _) =>
                {
                    reg.Dispose();
                    var choice = form.Choice;
                    form.Dispose();
                    tcs.TrySetResult(choice);
                };
                form.Show();
                reg = dismiss.Register(() => ui.Post(_ =>
                {
                    if (!form.IsDisposed)
                    {
                        form.Close();
                    }
                }, null));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }
}
