using Xunit;

namespace ClippysRevenge.Core.Tests;

public class InviteConfirmViewTests
{
    private static readonly InviteAttendee Jane = new("Doe, Jane", "jane@x.edu", false);

    private static InviteConfirmRequest Req(
        string organizer = "Me <me@x.edu>", bool shared = false, string subject = "Sync",
        DateTime? start = null, DateTime? end = null, string? location = null, bool teams = false,
        IReadOnlyList<InviteAttendee>? recipients = null, string? body = "hi") =>
        new(organizer, shared, subject,
            start ?? new DateTime(2024, 10, 15, 13, 0, 0),
            end ?? new DateTime(2024, 10, 15, 15, 0, 0),
            location, teams, recipients ?? [Jane], body);

    private static InviteConfirmView Make(InviteConfirmRequest r, string? tz = "PT", int ws = 8, int we = 17) =>
        InviteConfirmView.Create(r, tz, ws, we);

    [Fact]
    public void V1_SameDay()
    {
        var v = Make(Req());
        Assert.Equal("Tue 10/15 1-3 PT", v.WhenShort);
        Assert.Equal("Tuesday, October 15, 2024, 1:00 PM – 3:00 PM PT", v.WhenFull);
        Assert.Equal(InviteConfirmTexts.Title, v.Title);
        Assert.Equal(InviteConfirmTexts.Heading, v.Heading);
    }

    [Fact]
    public void V2_HalfHour()
    {
        var v = Make(Req(start: new DateTime(2024, 10, 16, 9, 30, 0), end: new DateTime(2024, 10, 16, 11, 0, 0)));
        Assert.Equal("Wed 10/16 9:30-11 PT", v.WhenShort);
    }

    [Fact]
    public void V3_AmPm() => Assert.Equal("Tue 10/15 1-3pm PT", Make(Req(), "PT", 6, 20).WhenShort);

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void V4_NoLabel(string? tz)
    {
        var v = Make(Req(), tz);
        Assert.Equal("Tue 10/15 1-3", v.WhenShort);
        Assert.EndsWith("3:00 PM", v.WhenFull);
    }

    [Fact]
    public void V5_MultiDay()
    {
        var v = Make(Req(start: new DateTime(2024, 10, 15, 22, 0, 0), end: new DateTime(2024, 10, 16, 1, 0, 0)));
        Assert.Null(v.WhenShort);
        Assert.Equal("Tuesday, October 15, 2024, 10:00 PM – Wednesday, October 16, 2024, 1:00 AM PT", v.WhenFull);
    }

    [Fact]
    public void V6_EndsAtMidnight()
    {
        var v = Make(Req(start: new DateTime(2024, 10, 15, 23, 0, 0), end: new DateTime(2024, 10, 16, 0, 0, 0)));
        Assert.Equal("Tue 10/15 11-12 PT", v.WhenShort);
        Assert.EndsWith("Wednesday, October 16, 2024, 12:00 AM PT", v.WhenFull);
    }

    [Fact]
    public void V7_BadHours() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Make(Req(), "PT", 17, 8));

    [Fact]
    public void V8_EndNotAfterStart()
    {
        var s = new DateTime(2024, 10, 15, 13, 0, 0);
        var e1 = Assert.Throws<ArgumentException>(() => Make(Req(start: s, end: s)));
        Assert.Contains("Meeting end must be after its start.", e1.Message);
        Assert.Throws<ArgumentException>(() => Make(Req(start: s, end: s.AddHours(-1))));
    }

    [Fact]
    public void V9_NoRecipients()
    {
        var e = Assert.Throws<ArgumentException>(() => Make(Req(recipients: [])));
        Assert.Contains("An invite needs at least one recipient.", e.Message);
    }

    [Fact]
    public void V10_Nulls()
    {
        Assert.Throws<ArgumentNullException>(() => Make(null!));
        Assert.Throws<ArgumentNullException>(() => Make(Req(subject: null!)));
        Assert.Throws<ArgumentNullException>(() => Make(Req(organizer: null!)));
        Assert.Throws<ArgumentNullException>(() => Make(Req() with { Recipients = null! }));
    }

    [Theory]
    [InlineData(false, "From: Sample, Bob <b@x.edu>")]
    [InlineData(true, "From: Sample, Bob <b@x.edu> (shared mailbox)")]
    public void V11_Organizer(bool shared, string expected) =>
        Assert.Equal(expected, Make(Req(organizer: "Sample, Bob <b@x.edu>", shared: shared)).OrganizerLine);

    [Fact]
    public void V12_Subject()
    {
        Assert.Equal("(no subject)", Make(Req(subject: "  ")).Subject);
        Assert.Equal("Sync", Make(Req(subject: " Sync ")).Subject);
    }

    [Fact]
    public void V13_Location()
    {
        Assert.Equal("Location: (none)", Make(Req(location: null)).LocationLine);
        Assert.Equal("Location: (none)", Make(Req(location: "")).LocationLine);
        Assert.Equal("Location: Room 1", Make(Req(location: " Room 1 ")).LocationLine);
    }

    [Fact]
    public void V14_Teams()
    {
        Assert.Equal("Teams meeting: yes", Make(Req(teams: true)).TeamsLine);
        Assert.Equal("Teams meeting: no", Make(Req(teams: false)).TeamsLine);
    }

    [Fact]
    public void V15_Recipients()
    {
        var v = Make(Req(recipients: [Jane, new InviteAttendee("", "bob@ext.org", true)]));
        Assert.Equal("To (2):", v.RecipientsHeader);
        Assert.Equal(new InviteAttendeeLine("Doe, Jane <jane@x.edu>", false), v.Recipients[0]);
        Assert.Equal(new InviteAttendeeLine("bob@ext.org — outside your organization", true), v.Recipients[1]);
        Assert.Equal("1 recipient is outside your organization.", v.ExternalSummary);
    }

    [Fact]
    public void V16_Summary()
    {
        Assert.Null(Make(Req()).ExternalSummary);
        var three = Enumerable.Range(0, 3).Select(i => new InviteAttendee("", $"a{i}@e.org", true)).ToList();
        Assert.Equal("3 recipients are outside your organization.", Make(Req(recipients: three)).ExternalSummary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public void V17_NoBody(string? body) =>
        Assert.Equal("(no message body)", Make(Req(body: body)).Body);

    [Fact]
    public void V18_BodyNormalized()
    {
        Assert.Equal("a\r\nb\r\nc\r\nd", Make(Req(body: "a\nb\r\nc\rd")).Body);
        Assert.Equal("  <b>hi</b>  ", Make(Req(body: "  <b>hi</b>  ")).Body);
    }

    [Theory]
    [InlineData(120, "2:00")]
    [InlineData(59.2, "1:00")]
    [InlineData(0.4, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(-5, "0:00")]
    [InlineData(900, "15:00")]
    public void V19_Countdown(double seconds, string clock) =>
        Assert.Equal($"Saved as draft in {clock} unless you choose.",
            InviteConfirmTexts.Countdown(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("a@x.edu", true, null, false)]
    [InlineData("a@x.edu", false, "me@x.edu", false)]
    [InlineData("a@X.EDU", false, "me@x.edu", false)]
    [InlineData("a@eecs.x.edu", false, "me@x.edu", false)]
    [InlineData("a@notx.edu", false, "me@x.edu", true)]
    [InlineData("a@x.edu.evil.com", false, "me@x.edu", true)]
    [InlineData("a@y.org", false, "me@x.edu", true)]
    [InlineData("a@x.edu", false, "me@eecs.x.edu", true)]
    [InlineData(null, false, "me@x.edu", true)]
    [InlineData("a@x.edu", false, null, true)]
    [InlineData("nonsense", false, "me@x.edu", true)]
    [InlineData("a@", false, "me@x.edu", true)]
    [InlineData("a@b@x.edu", false, "me@x.edu", true)]
    public void V20_IsExternal(string? email, bool exchange, string? org, bool expected) =>
        Assert.Equal(expected, InviteAttendees.IsExternal(email, exchange, org));
}
