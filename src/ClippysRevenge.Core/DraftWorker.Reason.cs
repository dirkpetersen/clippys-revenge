using Microsoft.Extensions.Logging;

namespace ClippysRevenge.Core;

// The reason line at the top of an AI draft (SPEC 25.4). The note is never logged or stored; only "yes"/"no" is logged.
public sealed partial class DraftWorker
{
    /// <summary>"" when !ai.ReasonLine (no log). Else DraftReasonLine.Html(note) and one Debug line
    /// "AI drafts: note: yes|no". Never throws.</summary>
    private string ReasonTop(AiDraftingSettings ai, string? note)
    {
        if (!ai.ReasonLine) return "";
        var top = DraftReasonLine.Html(note);
        try { _log?.LogDebug("AI drafts: note: {Note}", top.Length > 0 ? "yes" : "no"); }
        catch (Exception) { /* logging is best effort */ }
        return top;
    }
}