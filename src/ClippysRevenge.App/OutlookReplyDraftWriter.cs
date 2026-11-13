using ClippysRevenge.Core;

namespace ClippysRevenge.App;

internal sealed class OutlookReplyDraftWriter(OutlookWorker worker, OutlookMail mail, OutlookStores stores) : IReplyDraftWriter
{
    public async Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        return await worker.RunAsync(app =>
        {
            var r = mail.CreateReplyDraft(app, plan);
            return new RepliedDraft(r.EntryId, r.Subject, r.Mailbox);
        }, WorkPriority.Interactive, ct).ConfigureAwait(false);
    }
}
