using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

internal sealed class OutlookDraftWriter(OutlookWorker worker, OutlookStores stores) : IDraftWriter
{
    public Task<CreatedDraft> CreateAsync(DraftPlan plan, CancellationToken ct) =>
        worker.RunAsync(app =>
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
        }, WorkPriority.Interactive, ct);
}
