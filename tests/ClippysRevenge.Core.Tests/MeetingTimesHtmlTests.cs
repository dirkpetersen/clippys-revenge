namespace ClippysRevenge.Core.Tests;

public class MeetingTimesHtmlTests
{
    private static readonly TimeZoneInfo PT = TestZones.Pacific();
    private static readonly TimeZoneInfo BER = TestZones.Berlin();
    private const string Open = "<span style=\"background-color:#ffff00\">";
    private const string B2 = "1. Tue 11/18&nbsp; 10:00–11:00 PT<br>2. Wed 11/19&nbsp; 14:00–15:00 PT";

    private static DateTime D(int day, int h, int m = 0) => new(2025, 11, day, h, m, 0);
    private static TimeRange R(DateTime a, DateTime b) => new(a, b);

    private static MeetingOfferSet Offers(IReadOnlyList<TimeRange> blocks, int n, bool europe)
    {
        var owner = OwnerCalendar.Unknown;
        return MeetingOffers.Build(blocks, owner, 60, n, PT, null, europe, europe ? BER : null);
    }

    private static MeetingOfferSet O2() =>
        Offers([R(D(18, 10), D(18, 11)), R(D(19, 14), D(19, 15))], 2, false);

    private static MeetingOfferSet Mixed() =>
        Offers([R(D(18, 8), D(18, 10)), R(D(19, 13), D(19, 14))], 3, true);

    [Fact]
    public void AloneInParagraph()
    {
        var html = DraftHtml.Sanitize("<p>Hi Bob,</p><p>[[meeting_times]]</p><p>Best,<br>Ann</p>");
        Assert.Equal("<p>Hi Bob,</p><p>" + B2 + "</p><p>Best,<br>Ann</p>", MeetingTimesHtml.Insert(html, O2()));
    }

    [Fact]
    public void Inline() =>
        Assert.Equal("<p>How about <br>" + B2 + "<br> then?</p>",
            MeetingTimesHtml.Insert(DraftHtml.Sanitize("<p>How about [[meeting_times]] then?</p>"), O2()));

    [Fact]
    public void BareAndSanitizedMatch()
    {
        Assert.Equal("<p>" + B2 + "</p>", MeetingTimesHtml.Insert("<p>[[meeting_times]]</p>", O2()));
        var s = MeetingTimesHtml.Insert(DraftHtml.Sanitize("<p>[[meeting_times]]</p>"), O2());
        Assert.Equal("<p>" + B2 + "</p>", s);
        Assert.DoesNotContain("background-color", s);
    }

    [Theory]
    [InlineData("[[Meeting_Times]]")]
    [InlineData("[[meeting times]]")]
    [InlineData("[[meeting-times]]")]
    [InlineData("[[ MEETING_TIMES ]]")]
    public void Variants(string ph)
    {
        var html = $"<p>{ph}</p>";
        Assert.True(MeetingTimesHtml.HasPlaceholder(html));
        Assert.Equal("<p>" + B2 + "</p>", MeetingTimesHtml.Insert(html, O2()));
    }

    [Fact]
    public void NotAPlaceholder() => Assert.False(MeetingTimesHtml.HasPlaceholder("<p>[[meetingtimes]]</p>"));

    [Fact]
    public void OnlyFirstPlaceholderReplaced()
    {
        var html = DraftHtml.Sanitize("<p>[[meeting_times]]</p><p>[[meeting_times]]</p>");
        var r = MeetingTimesHtml.Insert(html, O2());
        Assert.Equal("<p>" + B2 + "</p><p>" + Open + "[[meeting_times]]</span></p>", r);
    }

    [Fact]
    public void NoPlaceholderPlacement()
    {
        Assert.Equal("<p>Hi</p><p>Sounds good.</p><p>" + B2 + "</p><p>Best,<br>Ann</p>",
            MeetingTimesHtml.Insert("<p>Hi</p><p>Sounds good.</p><p>Best,<br>Ann</p>", O2()));
        Assert.Equal("<p>Sure.</p><p>" + B2 + "</p>", MeetingTimesHtml.Insert("<p>Sure.</p>", O2()));
        Assert.Equal("Sure.<br>Ann<p>" + B2 + "</p>", MeetingTimesHtml.Insert("Sure.<br>Ann", O2()));
        const string nested = "<blockquote><p>a</p><p>b</p></blockquote>";
        Assert.Equal(nested + "<p>" + B2 + "</p>", MeetingTimesHtml.Insert(nested, O2()));
    }

    [Fact]
    public void SecondList()
    {
        var r = MeetingTimesHtml.Insert("<p>[[meeting_times]]</p>", Mixed());
        Assert.Contains("(17:00–18:00 CET)<br><br>Outside European office hours:<br>2. Tue 11/18&nbsp; 09:00–10:00 PT&nbsp; (18:00–19:00 CET)", r);
        var r2 = MeetingTimesHtml.Insert("<p>[[meeting_times]]</p>", Mixed(), "Außerhalb der Bürozeiten");
        Assert.Contains("<br><br>Außerhalb der Bürozeiten:<br>2. ", r2);

        var only = Offers([R(D(19, 13), D(19, 15))], 2, true);
        Assert.StartsWith("<p>Outside European office hours:<br>1. ", MeetingTimesHtml.Insert("<p>[[meeting_times]]</p>", only));
    }

    [Fact]
    public void HeadingEscaping()
    {
        var r = MeetingTimesHtml.Insert("<p>[[meeting_times]]</p>", Mixed(), "Fuera de horario & más");
        Assert.Contains("Fuera de horario &amp; más:", r);
        var r2 = MeetingTimesHtml.Insert("<p>[[meeting_times]]</p>", Mixed(), "x <b>y</b>");
        Assert.Contains("x by/b:", r2);
        Assert.DoesNotContain("<b>", r2);
    }

    [Fact]
    public void FailurePlaceholders()
    {
        Assert.Equal("<p>" + Open + "[[add meeting times: Clippy couldn't read your calendar]]</span></p>",
            MeetingTimesHtml.InsertPlaceholder("<p>[[meeting_times]]</p>", MeetingTimesHtml.CalendarUnreadable));
        var text = MeetingTimesHtml.NoFreeTime(new DateOnly(2025, 11, 18), new DateOnly(2025, 11, 28));
        Assert.Equal("[[add meeting times: no free time found between 2025-11-18 and 2025-11-28]]", text);
        Assert.Equal("<p>Hi</p><p>Sounds good.</p><p>" + Open + text + "</span></p><p>Best,<br>Ann</p>",
            MeetingTimesHtml.InsertPlaceholder("<p>Hi</p><p>Sounds good.</p><p>Best,<br>Ann</p>", text));
    }

    [Fact]
    public void PlainTextForm()
    {
        var html = DraftHtml.Sanitize("<p>Hi Bob,</p><p>[[meeting_times]]</p><p>Best,<br>Ann</p>");
        Assert.Equal("Hi Bob,\n1. Tue 11/18 10:00–11:00 PT\n2. Wed 11/19 14:00–15:00 PT\nBest,\nAnn",
            DraftHtml.ToPlainText(MeetingTimesHtml.Insert(html, O2())));
    }

    [Fact]
    public void Guards()
    {
        Assert.Throws<ArgumentException>(() => MeetingTimesHtml.Insert("<p>x</p>", MeetingOfferSet.Empty));
        Assert.Throws<ArgumentNullException>(() => MeetingTimesHtml.Insert(null!, O2()));
        Assert.Throws<ArgumentNullException>(() => MeetingTimesHtml.HasPlaceholder(null!));
    }

    [Fact]
    public void HighlightSpanMatchesDraftHtml()
    {
        Assert.Equal(Open, DraftHtml.HighlightOpen);
        Assert.Equal("</span>", DraftHtml.HighlightClose);
        Assert.True(MeetingTimesHtml.HasPlaceholder(DraftHtml.HighlightOpen + "[[meeting_times]]" + DraftHtml.HighlightClose));
    }
}
