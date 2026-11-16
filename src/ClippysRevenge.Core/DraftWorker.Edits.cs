using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

// Recording the text of a saved AI draft for learning from edits (SPEC 27.2). Never logs the text.
public sealed partial class DraftWorker
{
    private int _editsNoticeDone;   // 0/1, Interlocked

    /// <summary>The one-time edits notice (SPEC 27.6, the 25.3 pattern). Never throws.</summary>
    private void EditsNotice(AiDraftingSettings ai)
    {
        if (!ai.LearnFromEdits || ai.EditsNoticeShown || !ai.HasConsent) return;
        if (Interlocked.Exchange(ref _editsNoticeDone, 1) != 0) return;
        try
        {
            _s.MarkNoticeShown?.Invoke(AiNotice.Edits);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: couldn't save that the edits notice was shown ({Type}).", ex.GetType().Name);
        }
        try { _s.Hub.Event(ActivityKind.Ai, EditTexts.Notice); }
        catch (Exception) { /* the notice is best effort */ }
        try { _s.Balloons.Post(EditTexts.Notice, BalloonKind.Info); }
        catch (Exception) { /* the notice is best effort */ }
    }

    /// <summary>SPEC 27.2. Writes one ai_draft_texts row to the OWN index. Never throws.</summary>
    private void RecordDraftText(string? sourceImid, string html, AttachmentCounts counts, RepliedDraft made)
    {
        try
        {
            var ai = _s.Settings().AiDrafting;
            EditsNotice(ai);
            if (!ai.LearnFromEdits || counts.UsedText) return;
            var imid = sourceImid?.Trim();
            if (string.IsNullOrEmpty(imid) || string.IsNullOrWhiteSpace(made.EntryId)) return;
            var text = EditTexts.Prepare(DraftHtml.ToPlainText(html), null);
            if (text.Length == 0) return;
            _s.Indexes.For(MailboxIndexes.OwnMailbox)
                .RecordAiDraftText(made.EntryId, made.Mailbox, imid, _time.GetUtcNow(), text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning("AI drafts: couldn't record a draft's text ({Type}).", ex.GetType().Name);
        }
    }
}
