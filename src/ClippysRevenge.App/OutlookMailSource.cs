using ClippysRevenge.Core;
using ModelContextProtocol;

namespace ClippysRevenge.App;

/// <summary>
/// The COM side of sync (<see cref="IMailSource"/>): every call is one Background work item on the Outlook worker.
/// Read only. Nothing is saved, sent or modified.
/// </summary>
internal sealed class OutlookMailSource : IMailSource, IReconcileSource
{
    private const int MaxPages = 10_000;

    private readonly OutlookWorker _worker;
    private readonly OutlookStores _stores;
    private readonly OutlookMail _mail;

    public OutlookMailSource(OutlookWorker worker, OutlookStores stores, OutlookMail mail)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(mail);
        _worker = worker;
        _stores = stores;
        _mail = mail;
    }

    private async Task<T> Run<T>(Func<Microsoft.Office.Interop.Outlook.Application, T> work, CancellationToken ct)
    {
        try
        {
            return await _worker.RunAsync(work, WorkPriority.Background, ct).ConfigureAwait(false);
        }
        catch (OutlookUnavailableException ex)
        {
            throw new MailSourceUnavailableException(ex.Message, ex);
        }
    }

    public Task<SourceMailbox?> OpenMailboxAsync(string mailbox, CancellationToken ct) =>
        Run<SourceMailbox?>(app =>
        {
            var key = MailboxIndexes.Normalize(mailbox);
            MailboxStore? found;
            try
            {
                found = StoreCatalog.Find(_stores.List(app), key);
            }
            catch (McpException)
            {
                return null;
            }
            return found is null ? null : new SourceMailbox(key, found.Smtp, found.StoreId, found.DisplayName);
        }, ct);

    public Task<SourceFolder?> GetFolderAsync(SourceMailbox mailbox, string folderPath, CancellationToken ct) =>
        Run<SourceFolder?>(app =>
        {
            try
            {
                var rf = _mail.ResolveFolder(app, mailbox.Key, folderPath);
                return new SourceFolder(rf.FolderEntryId, rf.Path, rf.StoreId, FolderPaths.IsUnderSentItems(rf.Path));
            }
            catch (MailFolderNotFoundException)
            {
                return null;
            }
        }, ct);

    private static ResolvedFolder ToResolved(SourceFolder f) =>
        new("", f.Path, f.FolderKey, f.StoreId, f.IsSent ? EventTimeField.Sent : EventTimeField.Received);

    private static MailRow ToRow(MailTableRow r) => new(r.EntryId, r.LastModified, r.EventTime, r.Unread);

    public async Task<IReadOnlyList<MailRow>> ListChangedAsync(SourceFolder folder, DateTimeOffset modifiedSince,
        DateTimeOffset cutoff, CancellationToken ct)
    {
        var rf = ToResolved(folder);
        var rows = new List<MailRow>();
        var from = modifiedSince;
        for (var page = 0; page < MaxPages; page++)
        {
            var current = from;
            var p = await Run(app => _mail.EnumerateChanged(app, rf, current, cutoff), ct).ConfigureAwait(false);
            rows.AddRange(p.Rows.Select(ToRow));
            if (!p.HasMore || p.Rows.Count == 0) break;
            from = p.Rows[^1].LastModified.AddSeconds(1);
        }
        return rows;
    }

    public async Task<IReadOnlyList<MailRow>> ListRangeAsync(SourceFolder folder, DateTimeOffset from,
        DateTimeOffset to, CancellationToken ct)
    {
        var rf = ToResolved(folder);
        var rows = new List<MailRow>();
        // The interface is inclusive on both ends; EnumerateRange excludes "to".
        var upper = to.AddSeconds(1);
        for (var page = 0; page < MaxPages; page++)
        {
            var current = upper;
            var p = await Run(app => _mail.EnumerateRange(app, rf, from, current), ct).ConfigureAwait(false);
            rows.AddRange(p.Rows.Select(ToRow));
            if (!p.HasMore || p.Rows.Count == 0) break;
            upper = p.Rows[^1].EventTime;
        }
        return rows;
    }

    public async Task<IReadOnlyList<IndexedEmail>> ReadAsync(SourceFolder folder, IReadOnlyList<string> entryIds,
        CancellationToken ct)
    {
        var rf = ToResolved(folder);
        var batch = await Run(app => _mail.ReadBatch(app, rf, entryIds), ct).ConfigureAwait(false);
        return batch.Emails;
    }

    // ---- IReconcileSource (daily reconcile, SPEC §10 step 5) ----

    public Task<IReadOnlyList<OutlookEntry>> ListEntryIdsAsync(string mailbox, ReconcileFolder folder,
        DateTimeOffset since, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return Run<IReadOnlyList<OutlookEntry>>(app =>
        {
            var rf = _mail.ResolveFolder(app, mailbox, folder.FolderPath);
            return _mail.ListEntryIds(app, rf, since).Select(e => new OutlookEntry(e.EntryId, e.Unread)).ToList();
        }, ct);
    }

    public Task<IReadOnlyList<IndexedEmail>> ReadForIndexAsync(string mailbox, ReconcileFolder folder,
        IReadOnlyList<string> entryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(entryIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entryIds.Count, OutlookMail.MaxReadBatch);
        return Run<IReadOnlyList<IndexedEmail>>(app =>
        {
            var rf = _mail.ResolveFolder(app, mailbox, folder.FolderPath);
            return _mail.ReadBatch(app, rf, entryIds).Emails;
        }, ct);
    }
}
