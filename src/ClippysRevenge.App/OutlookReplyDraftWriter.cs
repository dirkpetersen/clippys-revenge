using ClippysRevenge.Core;

namespace ClippysRevenge.App;

internal sealed class OutlookReplyDraftWriter(OutlookWorker worker, OutlookMail mail) : IReplyDraftWriter
{
    public Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct) =>
        worker.RunAsync(app =>
        {
            var r = mail.CreateReplyDraft(app, plan);
            return new RepliedDraft(r.EntryId, r.Subject, r.Mailbox);
        }, WorkPriority.Interactive, ct);
}
