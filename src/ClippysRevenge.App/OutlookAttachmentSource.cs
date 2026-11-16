using ClippysRevenge.Core;

namespace ClippysRevenge.App;

/// <summary>SPEC 26.8 seam over OutlookWorker. Logs nothing.</summary>
internal sealed class OutlookAttachmentSource(OutlookWorker worker, OutlookMail mail, OutlookStores stores,
    AiFiles files, OneDriveRegistry registry) : IAttachmentSource
{
    /// <exception cref="OutlookUnavailableException"/>
    public async Task<AiAttachmentRead?> ReadAsync(string mailbox, string entryId, AttachmentInfo wanted, long maxBytes,
        WorkPriority priority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(entryId);
        ArgumentNullException.ThrowIfNull(wanted);
        await stores.EnsureResolvedAsync(priority, ct).ConfigureAwait(false);
        return await worker.RunAsync(app => mail.ReadAttachmentForAi(app, mailbox, entryId, wanted, maxBytes, files),
            priority, ct).ConfigureAwait(false);
    }

    /// <summary>registry.Read(); any exception gives []. Never throws.</summary>
    public IReadOnlyList<SyncedRoot> SyncedRoots()
    {
        try
        {
            return registry.Read();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
