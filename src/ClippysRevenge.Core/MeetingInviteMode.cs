namespace ClippysRevenge.Core;

/// <summary>How send_meeting_invite behaves (SPEC §5.2, §11.9).</summary>
public enum MeetingInviteMode
{
    Send,
    DraftOnly,
    Disabled,
}
