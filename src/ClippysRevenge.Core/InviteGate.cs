namespace ClippysRevenge.Core;

/// <summary>The button the user clicked in InviteConfirmForm (§6.6).</summary>
public enum InviteChoice { Send, SaveDraft, Cancel }

/// <summary>What send_meeting_invite must do.</summary>
public enum InviteAction { Send, SaveDraft, Cancel, Refuse }

/// <summary>Why a meeting is saved unsent.</summary>
public enum InviteDraftReason { UserChoice, Timeout, DraftOnlyMode }

/// <summary>The outcome of <see cref="InviteGate.Decide"/>.</summary>
public sealed class InviteDecision : IEquatable<InviteDecision>
{
    /// <summary>The action to take.</summary>
    public InviteAction Action { get; }

    /// <summary>Non-null if and only if Action is SaveDraft.</summary>
    public InviteDraftReason? DraftReason { get; }

    /// <summary>Non-null if and only if Action is Refuse. Text for the McpException.</summary>
    public string? RefuseReason { get; }

    /// <summary>Wire code for the §11.9 result "reason" field, or null.</summary>
    public string? ReasonCode => DraftReason switch
    {
        InviteDraftReason.UserChoice => "user_choice",
        InviteDraftReason.Timeout => "timeout",
        InviteDraftReason.DraftOnlyMode => "draft_only_mode",
        _ => null,
    };

    /// <summary>Send the invite.</summary>
    public static InviteDecision Send { get; } = new(InviteAction.Send, null, null);

    /// <summary>Create nothing.</summary>
    public static InviteDecision Cancel { get; } = new(InviteAction.Cancel, null, null);

    /// <summary>Save the meeting unsent.</summary>
    public static InviteDecision SaveDraft(InviteDraftReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        return new InviteDecision(InviteAction.SaveDraft, reason, null);
    }

    /// <summary>Refuse with a message for the model.</summary>
    public static InviteDecision Refuse(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A refuse reason is required.", nameof(reason));
        }
        return new InviteDecision(InviteAction.Refuse, null, reason);
    }

    private InviteDecision(InviteAction action, InviteDraftReason? draftReason, string? refuseReason)
    {
        Action = action;
        DraftReason = draftReason;
        RefuseReason = refuseReason;
    }

    public bool Equals(InviteDecision? other) =>
        other is not null && Action == other.Action && DraftReason == other.DraftReason
        && string.Equals(RefuseReason, other.RefuseReason, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as InviteDecision);

    public override int GetHashCode() => HashCode.Combine(Action, DraftReason, RefuseReason);

    public override string ToString() => Action switch
    {
        InviteAction.SaveDraft => $"SaveDraft({DraftReason})",
        InviteAction.Refuse => $"Refuse({RefuseReason})",
        _ => Action.ToString(),
    };
}

/// <summary>
/// Pure gate in front of the one real send (SPEC §6.6, §11.9). The most restrictive
/// input wins; Send is returned for exactly one input combination.
/// </summary>
public static class InviteGate
{
    public const string DisabledMessage =
        "Meeting invites are disabled in Clippy's Revenge. Ask the user to " +
        "enable them in the tray settings (Meeting invite mode: Send or " +
        "Draft only), then try again.";

    /// <summary>
    /// True only for MeetingInviteMode.Send: the tool must show the confirm dialog
    /// and await it before calling Decide.
    /// </summary>
    public static bool RequiresConfirmation(MeetingInviteMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return mode == MeetingInviteMode.Send;
    }

    /// <summary>Maps mode, user choice and timeout to the action to take.</summary>
    public static InviteDecision Decide(MeetingInviteMode mode, InviteChoice? userChoice, bool timedOut)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (userChoice is { } c && !Enum.IsDefined(c))
        {
            throw new ArgumentOutOfRangeException(nameof(userChoice));
        }

        if (mode == MeetingInviteMode.Disabled)
        {
            return InviteDecision.Refuse(DisabledMessage);
        }
        if (userChoice == InviteChoice.Cancel)
        {
            return InviteDecision.Cancel;
        }
        if (mode == MeetingInviteMode.DraftOnly)
        {
            return InviteDecision.SaveDraft(InviteDraftReason.DraftOnlyMode);
        }
        if (timedOut)
        {
            return InviteDecision.SaveDraft(InviteDraftReason.Timeout);
        }
        return userChoice switch
        {
            InviteChoice.Send => InviteDecision.Send,
            InviteChoice.SaveDraft => InviteDecision.SaveDraft(InviteDraftReason.UserChoice),
            _ => InviteDecision.Cancel,
        };
    }
}
