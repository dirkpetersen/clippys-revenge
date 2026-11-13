using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;

namespace ClippysRevenge.App;

internal sealed class OutlookReplyDraftWriter(OutlookWorker worker, OutlookMail mail, OutlookStores stores, ILogger? log = null) : IReplyDraftWriter
{
    public async Task<RepliedDraft> ReplyAsync(ReplyPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Enum.IsDefined(plan.Priority))
            throw new ArgumentOutOfRangeException(nameof(plan));
        await stores.EnsureResolvedAsync(plan.Priority, ct).ConfigureAwait(false);
        var r = await worker.RunAsync(app => mail.CreateReplyDraft(app, plan), plan.Priority, ct).ConfigureAwait(false);
        if (!r.MarkerSet)
            log?.LogWarning("Could not set the ClippyDraft marker on a draft.");
        return new RepliedDraft(r.EntryId, r.Subject, r.Mailbox);
    }
}
