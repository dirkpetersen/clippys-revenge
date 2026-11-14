using ClippysRevenge.Core;

namespace ClippysRevenge.App;

internal sealed class OutlookFreeBusySource(OutlookWorker worker) : IFreeBusySource
{
    public Task<Recipient> GetCurrentUserAsync(WorkPriority priority, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.GetCurrentUser(app), priority, ct);

    public Task<string?> GetFreeBusyAsync(string email, DateTime from, DateTime to, int intervalMinutes,
        WorkPriority priority, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.GetFreeBusy(app, email, from, to, intervalMinutes), priority, ct);
}
