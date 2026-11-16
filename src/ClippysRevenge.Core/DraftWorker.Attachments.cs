using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

// Attachment text for drafting prompts (SPEC 26.8). Counts only in logs; nothing is stored.
public sealed partial class DraftWorker
{
    /// <summary>The email being answered. Attachments: the index or live list, inline rows included.</summary>
    private sealed record AttachmentAnchor(string EntryId, string? Topic, DateTimeOffset Time, string? Subject,
        string? Body, string? SenderName, string? SenderEmail, IReadOnlyList<AttachmentInfo> Attachments);

    private int _attachmentsNoticeDone;   // 0/1, Interlocked

    /// <summary>Behavior A. AttachmentSet.Empty (and no source, index or notice call) when the gate is closed.
    /// Never throws except OperationCanceledException when ct is cancelled.</summary>
    private async Task<AttachmentSet> CollectAttachmentsAsync(AiDraftingSettings ai, EmailIndex index, string key,
        AttachmentAnchor anchor, string? signatureText, AiIdentity identity, string userSmtp, WorkPriority priority,
        string? excludeEntryId, CancellationToken ct)
    {
        if (!ai.ReadAttachments || _s.Attachments is null || !ai.HasConsent) return AttachmentSet.Empty;
        AttachmentsNotice(ai);
        try
        {
            var thread = index.ThreadAttachments(anchor.Topic, anchor.Time, anchor.EntryId);
            if (excludeEntryId is not null)
                thread = thread.Where(r => !string.Equals(r.EntryId, excludeEntryId, StringComparison.Ordinal)).ToList();
            var own = userSmtp.Length > 0 ? identity with { OwnSmtp = userSmtp } : identity;
            var request = new AttachmentRequest(key, anchor.EntryId, anchor.Subject, anchor.Body, anchor.SenderName,
                anchor.SenderEmail, anchor.Time, anchor.Attachments ?? [], thread, own, signatureText, priority, _s.Zone);
            var set = await _s.Attachments.CollectAsync(request, ct).ConfigureAwait(false);
            try
            {
                var c = set.Counts;
                _log?.LogDebug("AI drafts: attachments {Read} read, {Truncated} truncated, {Skipped} skipped, {Links} links, {Chars} chars in {Ms} ms",
                    c.Read, c.Truncated, c.Skipped, c.Links, c.Chars, c.ElapsedMs);
            }
            catch (Exception) { /* logging is best effort */ }
            return set;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: reading attachments failed ({Type}).", ex.GetType().Name);
            return AttachmentSet.Empty;
        }
    }

    /// <summary>The one-time notice (SPEC 26.10, the 25.3 pattern). Never throws.</summary>
    private void AttachmentsNotice(AiDraftingSettings ai)
    {
        if (!ai.ReadAttachments || ai.AttachmentsNoticeShown || !ai.HasConsent || _s.Attachments is null) return;
        if (Interlocked.Exchange(ref _attachmentsNoticeDone, 1) != 0) return;
        try
        {
            _s.MarkNoticeShown?.Invoke(AiNotice.Attachments);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: couldn't save that the attachments notice was shown ({Type}).", ex.GetType().Name);
        }
        try { _s.Hub.Event(ActivityKind.Ai, DraftAttachmentTexts.Notice); }
        catch (Exception) { /* the notice is best effort */ }
        try { _s.Balloons.Post(DraftAttachmentTexts.Notice, BalloonKind.Info); }
        catch (Exception) { /* the notice is best effort */ }
    }
}
