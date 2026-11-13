using ModelContextProtocol;

namespace ClippysRevenge.Core.Tests;

public class MeetingRequestTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "Test-7", "Test-7");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.FromHours(-7));
    private const string R2 = "Sample, Bob <bob@example.edu>; ext@other.org";
    private const string StartHint = "2026-03-01T14:30";

    private static MeetingRequest Make(string? recipients = R2, string? subject = "Sync", string? start = "2026-11-17T13:00",
        string? end = "2026-11-17T14:30", string? body = null, string? location = null, string? mailbox = null) =>
        MeetingRequest.Create(recipients, subject, start, end, body, location, false, mailbox, Tz, Now);

    private static string Err(Func<MeetingRequest> f) => Assert.Throws<McpException>(f).Message;

    [Fact]
    public void Valid_input()
    {
        var r = Make(subject: " Sync ");
        Assert.Equal([new Recipient("Sample, Bob", "bob@example.edu"), new Recipient("", "ext@other.org")], r.Recipients);
        Assert.Equal("Sync", r.Subject);
        Assert.Equal(new DateTime(2026, 11, 17, 13, 0, 0), r.Start);
        Assert.Equal(DateTimeKind.Unspecified, r.Start.Kind);
        Assert.Equal(new DateTime(2026, 11, 17, 14, 30, 0), r.End);
        Assert.Equal("", r.Body);
        Assert.Null(r.Location);
        Assert.Equal("", r.Mailbox);
    }

    [Theory]
    [InlineData("2026-11-17T13:00:00-07:00")]
    [InlineData("2026-11-17T20:00:00Z")]
    [InlineData("2026-11-17T16:00:00-04:00")]
    [InlineData("2026-11-17 13:00")]
    [InlineData("2026-11-17T13:00:00.0000000")]
    public void Start_forms(string start) =>
        Assert.Equal(new DateTime(2026, 11, 17, 13, 0, 0), Make(start: start).Start);

    [Fact]
    public void Date_only_start_is_rejected() =>
        Assert.Equal("start \"2026-11-17\" is not a valid date-time. Use an ISO date-time like 2026-03-01T14:30 (local time), or the start and end of a find_free_times suggestion.",
            Err(() => Make(start: "2026-11-17")));

    [Fact]
    public void Garbage_end() =>
        Assert.Equal("end \"tomorrow 3pm\" is not a valid date-time. Use an ISO date-time like 2026-03-01T14:30 (local time), or the start and end of a find_free_times suggestion.",
            Err(() => Make(end: "tomorrow 3pm")));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Empty_start(string? start) =>
        Assert.Equal("start is empty. Pass a local date-time like 2026-03-01T14:30, or the start of a find_free_times suggestion.",
            Err(() => Make(start: start)));

    [Fact]
    public void Empty_end() =>
        Assert.Equal("end is empty. Pass a local date-time like 2026-03-01T15:30, or the end of a find_free_times suggestion.",
            Err(() => Make(end: null)));

    [Fact]
    public void End_not_after_start()
    {
        Assert.Equal("end (2026-11-17 13:00) must be after start (2026-11-17 13:00).",
            Err(() => Make(end: "2026-11-17T13:00")));
        Assert.Equal("end (2026-11-17 12:00) must be after start (2026-11-17 13:00).",
            Err(() => Make(end: "2026-11-17T12:00")));
    }

    [Fact]
    public void Past_end()
    {
        Assert.Equal("This meeting ends in the past (2026-11-12 09:00). Pick a future time.",
            Err(() => Make(start: "2026-11-12T08:00", end: "2026-11-12T09:00")));
        Assert.Equal(new DateTime(2026, 11, 12, 8, 0, 0), Make(start: "2026-11-12T08:00", end: "2026-11-12T10:00").Start);
    }

    [Fact]
    public void Out_of_range_is_mcp_exception() =>
        Err(() => Make(start: "0001-01-01T00:30:00+05:00"));

    [Fact]
    public void Garbage_is_cut_to_100_chars()
    {
        var garbage = new string('x', 150);
        var msg = Err(() => Make(start: garbage));
        Assert.StartsWith("start \"" + new string('x', 100) + "\" is not", msg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ;")]
    public void Empty_recipients(string? r) =>
        Assert.Equal("recipients is empty. Pass at least one attendee as \"Name <email>\" or a bare email, separated by \";\".",
            Err(() => Make(recipients: r)));

    [Fact]
    public void Recipient_without_email() =>
        Assert.Equal("Recipient \"Bob Sample\" has no valid email address. Use \"Name <email>\" or a bare email, and separate recipients with \";\".",
            Err(() => Make(recipients: "Bob Sample")));

    [Fact]
    public void Duplicates_collapse() => Assert.Single(Make(recipients: "a@x.org; A@X.org").Recipients);

    [Fact]
    public void Subject_rules()
    {
        Assert.Equal("subject is empty. Give the meeting a subject.", Err(() => Make(subject: "   ")));
        Assert.Equal("a b", Make(subject: "a\r\n  b").Subject);
    }

    [Fact]
    public void Body_rules()
    {
        Assert.Equal("", Make(body: null).Body);
        Assert.Equal("a\r\nb\r\nc\r\nd", Make(body: "a\nb\rc\r\nd").Body);
        Assert.Equal("  x  ", Make(body: "  x  ").Body);
    }

    [Fact]
    public void Location_rules()
    {
        Assert.Null(Make(location: "  ").Location);
        Assert.Equal("Room 1", Make(location: " Room 1 ").Location);
    }

    [Fact]
    public void Mailbox_rules()
    {
        Assert.Equal("shared@x.org", Make(mailbox: " Shared@X.org ").Mailbox);
        Assert.Equal("", Make(mailbox: null).Mailbox);
    }

    [Fact]
    public void Validation_order() =>
        Assert.StartsWith("recipients is empty", Err(() => Make(recipients: "", subject: "")));
}
