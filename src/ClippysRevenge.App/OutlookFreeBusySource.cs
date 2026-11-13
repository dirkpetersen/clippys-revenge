using ClippysRevenge.Core;

namespace ClippysRevenge.App;

internal sealed class OutlookFreeBusySource(OutlookWorker worker) : IFreeBusySource
{
    public Task<Recipient> GetCurrentUserAsync(CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.GetCurrentUser(app), WorkPriority.Interactive, ct);

    public Task<string?> GetFreeBusyAsync(string email, DateTime from, DateTime to, int intervalMinutes, CancellationToken ct) =>
        worker.RunAsync(app => OutlookCalendar.GetFreeBusy(app, email, from, to, intervalMinutes), WorkPriority.Interactive, ct);
}
