using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Microsoft.Extensions.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

internal sealed class OutlookDraftWriter(OutlookWorker worker, OutlookStores stores, ILogger? log = null) : IDraftWriter
{
    public async Task<CreatedDraft> CreateAsync(DraftPlan plan, CancellationToken ct)
    {
        if (plan.Mailbox.Length > 0)
            await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        var created = await worker.RunAsync(app =>
        {
            Outlook.Store? store = plan.Mailbox.Length == 0 ? null : stores.Get(app, plan.Mailbox);
            try
            {
                return OutlookMail.CreateDraft(app, store, plan);
            }
            finally
            {
                if (store is not null) Marshal.ReleaseComObject(store);
            }
        }, WorkPriority.Interactive, ct).ConfigureAwait(false);
        if (!created.MarkerSet)
            log?.LogWarning("Could not set the ClippyDraft marker on a draft.");
        return created;
    }
}
