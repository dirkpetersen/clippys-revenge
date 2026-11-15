namespace ClippysRevenge.Core;

/// <summary>SPEC 23.5. Evaluate returns Gone..Movable; Moved and NoDeletedItems only come from
/// IAiMailbox.MoveObsoleteAiDraftAsync.</summary>
public enum ObsoleteDraftStatus { Gone, NotInDrafts, Sent, NotMarked, Open, Changed, Movable, Moved, NoDeletedItems }

/// <summary>What a read-only COM look at a tracked AI draft found (SPEC 23.5). Plain data, no COM objects.</summary>
/// <param name="InDrafts">Its parent folder is the default Drafts folder of its own store.</param>
/// <param name="Marker">The raw ClippyDraft value (normalized by the guard).</param>
/// <param name="Open">Open in an Inspector or as an inline response, or that check failed.</param>
/// <param name="Fingerprint">DraftFingerprint of the item as it is now.</param>
/// <param name="PlainBody">The plain-text body, cut to MaxPlainBodyChars by the COM side.</param>
public sealed record AiDraftInfo(bool InDrafts, bool Sent, string? Marker, string? MessageClass, bool Open,
    string Fingerprint, string Subject, string PlainBody)
{
    public const int MaxPlainBodyChars = 20_000;
}

/// <summary>Result of MoveObsoleteAiDraftAsync. Subject: the draft's subject ("" when it couldn't be opened).</summary>
public sealed record ObsoleteDraftMove(ObsoleteDraftStatus Status, string Subject);

public static class AiDraftGuard
{
    /// <summary>SPEC 23.5 table, first match wins. Pure.</summary>
    /// <exception cref="ArgumentNullException">expectedFingerprint is null.</exception>
    public static ObsoleteDraftStatus Evaluate(AiDraftInfo? info, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        if (info is null) return ObsoleteDraftStatus.Gone;
        if (!info.InDrafts) return ObsoleteDraftStatus.NotInDrafts;
        if (info.Sent) return ObsoleteDraftStatus.Sent;
        if (DraftMarker.Normalize(info.Marker) != DraftMarker.Ai
            || !string.Equals(info.MessageClass, "IPM.Note", StringComparison.OrdinalIgnoreCase))
            return ObsoleteDraftStatus.NotMarked;
        if (info.Open) return ObsoleteDraftStatus.Open;
        if (expectedFingerprint.Length == 0
            || !string.Equals(info.Fingerprint ?? "", expectedFingerprint, StringComparison.Ordinal))
            return ObsoleteDraftStatus.Changed;
        return ObsoleteDraftStatus.Movable;
    }
}
