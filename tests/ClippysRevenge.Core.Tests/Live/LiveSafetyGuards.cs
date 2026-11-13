using ClippysRevenge.App;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>Fails the test if the confirmation dialog is ever requested (DraftOnly must never ask).</summary>
internal sealed class RefusingConfirmer : IInviteConfirmer
{
    public Task<InviteConfirmation> ConfirmAsync(InvitePreview preview, TimeSpan timeout, CancellationToken ct) =>
        throw new InvalidOperationException("Live test: the invite confirmer must not be called in DraftOnly mode.");
}

/// <summary>
/// Wraps the real calendar. Refuses anything but SaveDraft BEFORE any COM code runs, so the one
/// production Send path in OutlookCalendar.CreateMeeting can never be reached from a live test.
/// </summary>
internal sealed class DraftOnlyCalendar(IMeetingCalendar inner) : IMeetingCalendar
{
    public InviteDecision? LastDecision { get; private set; }

    public Task<ResolvedInvite> ResolveAsync(MeetingRequest request, CancellationToken ct) =>
        inner.ResolveAsync(request, ct);

    public Task<CreatedMeeting> CreateAsync(MeetingRequest request, InviteDecision decision, CancellationToken ct)
    {
        LastDecision = decision;
        if (decision.Action != InviteAction.SaveDraft)
            throw new InvalidOperationException("Live test refused: only SaveDraft is allowed.");
        return inner.CreateAsync(request, decision, ct);
    }
}
