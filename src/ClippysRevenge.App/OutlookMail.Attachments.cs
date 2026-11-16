using System.Runtime.InteropServices;
using ClippysRevenge.Core;
using ModelContextProtocol;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.App;

/// <summary>Attachment export (SPEC §11.3, §11.4). Worker thread only. Content goes only to the chosen file on disk.</summary>
internal sealed partial class OutlookMail
{
    internal SavedAttachment SaveAttachment(Outlook.Application app, string id, string? filename, int? position, string dir)
    {
        var item = GetItem(app, id);
        try
        {
            var live = ReadAttachments(item);
            var target = AttachmentSelection.SelectOne(live, filename, position);
            return SaveOne(item, target, dir);
        }
        finally
        {
            Release(item);
        }
    }

    internal IReadOnlyList<AttachmentSaveOutcome> SaveAttachments(
        Outlook.Application app, string id, IReadOnlyList<AttachmentInfo> wanted, string dir)
    {
        var item = GetItem(app, id);
        try
        {
            var live = ReadAttachments(item);
            var outcomes = new List<AttachmentSaveOutcome>(wanted.Count);
            foreach (var w in wanted)
            {
                var m = AttachmentSelection.MatchLive(live, w);
                if (m is null)
                {
                    outcomes.Add(new AttachmentSaveOutcome(w, null,
                        $"Attachment \"{w.FileName}\" (position {w.Position}) is no longer on this email. It may have changed since it was indexed; run search_emails again."));
                    continue;
                }
                try
                {
                    outcomes.Add(new AttachmentSaveOutcome(w, SaveOne(item, m, dir), null));
                }
                catch (McpException ex) when (ex is not OutlookUnavailableException)
                {
                    outcomes.Add(new AttachmentSaveOutcome(w, null, ex.Message));
                }
            }
            return outcomes;
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>Worker thread only. See IAttachmentLinkSaver. abandoned: set by the caller after it gave up
    /// (timeout/cancel); a save that finishes afterwards deletes its file.</summary>
    internal SavedAttachment SaveAttachmentForLink(Outlook.Application app, string id, string? filename, int? position,
        string tempPath, long maxBytes, CancellationToken abandoned)
    {
        if (!LinkPaths.IsTempFileName(Path.GetFileName(tempPath)) || !Path.IsPathFullyQualified(tempPath))
            throw new ArgumentException("tempPath must be a link temp file.", nameof(tempPath));
        abandoned.ThrowIfCancellationRequested();

        var item = GetItem(app, id);
        try
        {
            var live = ReadAttachments(item);
            var target = AttachmentSelection.SelectOne(live, filename, position);
            if (target.Size is { } est && est > maxBytes)
                throw new AttachmentLinkException(LinkRefusal.TooBig, LinkTexts.TooBig(est));

            var atts = item.Attachments;
            Outlook.Attachment? att = null;
            try
            {
                att = atts[target.Position];
                att.SaveAsFile(tempPath);
                var length = new FileInfo(tempPath).Length;
                if (length > maxBytes)
                {
                    DeleteQuietly(tempPath);
                    throw new AttachmentLinkException(LinkRefusal.TooBig, LinkTexts.TooBig(length));
                }
                if (abandoned.IsCancellationRequested)
                {
                    DeleteQuietly(tempPath);
                    throw new OperationCanceledException(abandoned);
                }
                return new SavedAttachment(tempPath, target.FileName, length);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                       && !ComErrors.IsOutlookGone(ex))
            {
                DeleteQuietly(tempPath);
                // IO messages contain the temp path: never put them in a result.
                var reason = ex is COMException ? ex.Message.TrimEnd('.') : ex.GetType().Name;
                throw new McpException(
                    $"Could not save \"{target.FileName}\" for the download link ({reason}). Try again, or use get_attachment.");
            }
            finally
            {
                Release(att);
                Release(atts);
            }
        }
        finally
        {
            Release(item);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The Core store retries via Discard.
        }
    }

    private static SavedAttachment SaveOne(Outlook.MailItem item, AttachmentInfo target, string dir)
    {
        // Computed on the worker thread right before SaveAsFile: saves are serialized, so names can't collide.
        var path = FileNames.UniquePath(dir, target.FileName);
        var atts = item.Attachments;
        Outlook.Attachment? att = null;
        try
        {
            att = atts[target.Position];
            att.SaveAsFile(path);
            return new SavedAttachment(path, target.FileName, new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                   && !ComErrors.IsOutlookGone(ex))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
            throw new McpException($"Could not save \"{target.FileName}\": {ex.Message}");
        }
        finally
        {
            Release(att);
            Release(atts);
        }
    }
}

internal sealed class OutlookAttachmentSaver(OutlookWorker worker, OutlookMail mail, OutlookStores stores) : IAttachmentSaver
{
    public async Task<SavedAttachment> SaveOneAsync(string id, string? filename, int? position, string dir, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        return await worker.RunAsync(app => mail.SaveAttachment(app, id, filename, position, dir), WorkPriority.Interactive, ct,
            OutlookStores.AttachmentTimeout).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AttachmentSaveOutcome>> SaveManyAsync(
        string id, IReadOnlyList<AttachmentInfo> wanted, string dir, CancellationToken ct)
    {
        await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
        return await worker.RunAsync(app => mail.SaveAttachments(app, id, wanted, dir), WorkPriority.Interactive, ct,
            OutlookStores.AttachmentTimeout).ConfigureAwait(false);
    }
}

internal sealed class OutlookAttachmentLinkSaver(OutlookWorker worker, OutlookMail mail, OutlookStores stores)
    : IAttachmentLinkSaver
{
    public async Task<SavedAttachment> SaveForLinkAsync(string id, string? filename, int? position, string tempPath,
        long maxBytes, CancellationToken ct)
    {
        using var abandon = new CancellationTokenSource();
        try
        {
            await stores.EnsureResolvedAsync(WorkPriority.Interactive, ct).ConfigureAwait(false);
            return await worker.RunAsync(
                app => mail.SaveAttachmentForLink(app, id, filename, position, tempPath, maxBytes, abandon.Token),
                WorkPriority.Interactive, ct, OutlookStores.AttachmentTimeout).ConfigureAwait(false);
        }
        catch
        {
            abandon.Cancel();   // before Core's Discard runs: a COM save still running deletes its own file
            throw;
        }
    }
}
