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

internal sealed class OutlookAttachmentSaver(OutlookWorker worker, OutlookMail mail) : IAttachmentSaver
{
    public Task<SavedAttachment> SaveOneAsync(string id, string? filename, int? position, string dir, CancellationToken ct) =>
        worker.RunAsync(app => mail.SaveAttachment(app, id, filename, position, dir), WorkPriority.Interactive, ct);

    public async Task<IReadOnlyList<AttachmentSaveOutcome>> SaveManyAsync(
        string id, IReadOnlyList<AttachmentInfo> wanted, string dir, CancellationToken ct) =>
        await worker.RunAsync(app => mail.SaveAttachments(app, id, wanted, dir), WorkPriority.Interactive, ct)
            .ConfigureAwait(false);
}
