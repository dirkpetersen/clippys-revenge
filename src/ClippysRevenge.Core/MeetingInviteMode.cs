namespace ClippysRevenge.Core;

/// <summary>How send_meeting_invite behaves (SPEC §5.2). Send is the default (0).</summary>
public enum MeetingInviteMode
{
    Send,
    DraftOnly,
    Disabled,
}
