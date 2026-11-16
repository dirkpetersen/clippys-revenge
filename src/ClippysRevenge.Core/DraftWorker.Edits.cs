using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

// Recording the text of a saved AI draft for learning from edits (SPEC 27.2). Never logs the text.
public sealed partial class DraftWorker
{
    /// <summary>SPEC 27.2. Writes one ai_draft_texts row to the OWN index. Never throws.</summary>
    private void RecordDraftText(string? sourceImid, string html, AttachmentCounts counts, RepliedDraft made)
    {
        try
        {
            if (!_s.Settings().AiDrafting.LearnFromEdits || counts.UsedText) return;
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
