using Xunit;

namespace ClippysRevenge.Core.Tests;

public class InviteGateTests
{
    private const MeetingInviteMode S = MeetingInviteMode.Send;
    private const MeetingInviteMode D = MeetingInviteMode.DraftOnly;
    private const MeetingInviteMode X = MeetingInviteMode.Disabled;

    private static InviteDecision Draft(InviteDraftReason r) => InviteDecision.SaveDraft(r);
    private static readonly InviteDecision Refused = InviteDecision.Refuse(InviteGate.DisabledMessage);

    public static TheoryData<MeetingInviteMode, InviteChoice?, bool, InviteDecision> Matrix()
    {
        var d = new TheoryData<MeetingInviteMode, InviteChoice?, bool, InviteDecision>();
        InviteChoice? none = null;
        InviteChoice? snd = InviteChoice.Send;
        InviteChoice? sav = InviteChoice.SaveDraft;
        InviteChoice? can = InviteChoice.Cancel;
        var to = InviteDraftReason.Timeout;
        var uc = InviteDraftReason.UserChoice;
        var dm = InviteDraftReason.DraftOnlyMode;

        d.Add(S, none, false, InviteDecision.Cancel);
        d.Add(S, none, true, Draft(to));
        d.Add(S, snd, false, InviteDecision.Send);
        d.Add(S, snd, true, Draft(to));
        d.Add(S, sav, false, Draft(uc));
        d.Add(S, sav, true, Draft(to));
        d.Add(S, can, false, InviteDecision.Cancel);
        d.Add(S, can, true, InviteDecision.Cancel);
        foreach (var t in new[] { false, true })
        {
            d.Add(D, none, t, Draft(dm));
            d.Add(D, snd, t, Draft(dm));
            d.Add(D, sav, t, Draft(dm));
            d.Add(D, can, t, InviteDecision.Cancel);
            d.Add(X, none, t, Refused);
            d.Add(X, snd, t, Refused);
            d.Add(X, sav, t, Refused);
            d.Add(X, can, t, Refused);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Decide_FullMatrix(MeetingInviteMode mode, InviteChoice? choice, bool timedOut, InviteDecision expected)
    {
        Assert.Equal(expected, InviteGate.Decide(mode, choice, timedOut));
    }

    [Fact]
    public void Matrix_Has24Rows()
    {
        Assert.Equal(24, Matrix().Count());
    }

    private static IEnumerable<(MeetingInviteMode, InviteChoice?, bool)> All()
    {
        var choices = new InviteChoice?[] { null }.Concat(Enum.GetValues<InviteChoice>().Select(c => (InviteChoice?)c));
        foreach (var m in Enum.GetValues<MeetingInviteMode>())
            foreach (var c in choices)
                foreach (var t in new[] { false, true })
                    yield return (m, c, t);
    }

    [Fact]
    public void Decide_ReturnsSendOnlyForSendModeSendChoiceNoTimeout()
    {
        var n = 0;
        foreach (var (m, c, t) in All())
        {
            n++;
            var isSend = InviteGate.Decide(m, c, t).Action == InviteAction.Send;
            Assert.Equal(m == S && c == InviteChoice.Send && !t, isSend);
        }
        Assert.Equal(24, n);
    }

    [Fact]
    public void Decide_Invariants()
    {
        foreach (var (m, c, t) in All())
        {
            var r = InviteGate.Decide(m, c, t);
            Assert.Equal(r.Action == InviteAction.SaveDraft, r.DraftReason is not null);
            Assert.Equal(r.Action == InviteAction.Refuse, r.RefuseReason is not null);
            Assert.Equal(r.Action == InviteAction.SaveDraft, r.ReasonCode is not null);
        }
    }

    [Fact]
    public void ReasonCode_Values()
    {
        Assert.Equal("user_choice", Draft(InviteDraftReason.UserChoice).ReasonCode);
        Assert.Equal("timeout", Draft(InviteDraftReason.Timeout).ReasonCode);
        Assert.Equal("draft_only_mode", Draft(InviteDraftReason.DraftOnlyMode).ReasonCode);
        Assert.Null(InviteDecision.Send.ReasonCode);
        Assert.Null(InviteDecision.Cancel.ReasonCode);
    }

    [Fact]
    public void Decide_Disabled_RefuseReasonIsDisabledMessage()
    {
        var r = InviteGate.Decide(X, null, false);
        Assert.Equal(InviteGate.DisabledMessage, r.RefuseReason);
        Assert.Contains("tray settings", r.RefuseReason);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    public void Decide_UndefinedMode_Throws(int mode)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => InviteGate.Decide((MeetingInviteMode)mode, InviteChoice.Send, false));
        Assert.Equal("mode", ex.ParamName);
    }

    [Fact]
    public void Decide_UndefinedChoice_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => InviteGate.Decide(S, (InviteChoice)42, false));
        Assert.Equal("userChoice", ex.ParamName);
    }

    [Theory]
    [InlineData(MeetingInviteMode.Send, true)]
    [InlineData(MeetingInviteMode.DraftOnly, false)]
    [InlineData(MeetingInviteMode.Disabled, false)]
    public void RequiresConfirmation(MeetingInviteMode mode, bool expected)
    {
        Assert.Equal(expected, InviteGate.RequiresConfirmation(mode));
    }

    [Fact]
    public void RequiresConfirmation_Undefined_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InviteGate.RequiresConfirmation((MeetingInviteMode)42));
    }

    [Fact]
    public void MeetingInviteMode_DefaultIsSend()
    {
        Assert.Equal(MeetingInviteMode.Send, default(MeetingInviteMode));
        Assert.Equal(new[] { S, D, X }, Enum.GetValues<MeetingInviteMode>());
    }

    [Fact]
    public void InviteDecision_Factories()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InviteDecision.SaveDraft((InviteDraftReason)42));
        Assert.ThrowsAny<ArgumentException>(() => InviteDecision.Refuse(null!));
        Assert.ThrowsAny<ArgumentException>(() => InviteDecision.Refuse(""));
        Assert.ThrowsAny<ArgumentException>(() => InviteDecision.Refuse("  "));
        Assert.Same(InviteDecision.Send, InviteDecision.Send);
        var a = Draft(InviteDraftReason.Timeout);
        var b = Draft(InviteDraftReason.Timeout);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(Draft(InviteDraftReason.UserChoice)));
        Assert.Equal("SaveDraft(Timeout)", a.ToString());
    }
}
