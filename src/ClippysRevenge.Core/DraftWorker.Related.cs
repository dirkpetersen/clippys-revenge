using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

// Related earlier mail in drafting prompts (SPEC 25.3). Nothing here logs or stores mail text, subjects, addresses or terms.
public sealed partial class DraftWorker
{
    private sealed record RelatedAnchor(string EntryId, string? Imid, string? Topic, DateTimeOffset Time,
        string? SenderEmail, string? Subject, string? Body);

    private int _relatedNoticeDone;   // 0/1, Interlocked

    /// <summary>Up to 5 related earlier emails from the anchor's own mailbox index (SPEC 25.3). Never throws except
    /// OperationCanceledException from the index call (none is raised today); a failed lookup gives no rows.</summary>
    private IReadOnlyList<RelatedRow> FindRelated(AiDraftingSettings ai, EmailIndex index, RelatedAnchor anchor,
        string? signatureText, string userSmtp, string mailboxSmtp)
    {
        if (!ai.RelatedMail) return [];
        RelatedNotice(ai);
        try
        {
            var own = MailText.OwnText(anchor.Body ?? "", signatureText);
            var terms = RelatedMail.Terms(anchor.Subject, own);
            var sender = anchor.SenderEmail?.Trim();
            if (string.IsNullOrEmpty(sender) || DraftRevisit.IsOwnAddress(sender, userSmtp, mailboxSmtp)) sender = null;
            var now = _time.GetUtcNow();
            var hits = index.RelatedCandidates(new RelatedQuery(anchor.EntryId, anchor.Imid, anchor.Topic, sender, terms,
                now.AddMonths(-RelatedMail.WindowMonths), anchor.Time));
            var rows = RelatedMail.Select(hits, now);
            _log?.LogDebug("AI drafts: related mail {Count} ({Terms} terms)", rows.Count, terms.Count);
            return rows;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: looking up related mail failed ({Type}).", ex.GetType().Name);
            return [];
        }
    }

    /// <summary>The one-time notice (SPEC 25.3): an Ai event and a balloon, right before the first prompt that may
    /// hold related mail. At most once per process. Never throws.</summary>
    private void RelatedNotice(AiDraftingSettings ai)
    {
        if (!ai.RelatedMail || ai.RelatedMailNoticeShown || !ai.HasConsent) return;
        if (Interlocked.Exchange(ref _relatedNoticeDone, 1) != 0) return;
        try
        {
            _s.MarkNoticeShown?.Invoke(AiNotice.RelatedMail);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("AI drafts: couldn't save that the related-mail notice was shown ({Type}).", ex.GetType().Name);
        }
        try { _s.Hub.Event(ActivityKind.Ai, RelatedMailTexts.Notice); }
        catch (Exception) { /* the notice is best effort */ }
        try { _s.Balloons.Post(RelatedMailTexts.Notice, BalloonKind.Info); }
        catch (Exception) { /* the notice is best effort */ }
    }
}
