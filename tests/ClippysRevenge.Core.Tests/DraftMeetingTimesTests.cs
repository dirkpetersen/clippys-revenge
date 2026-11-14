using System.Text.Json;

namespace ClippysRevenge.Core.Tests;

public class DraftMeetingTimesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string H(string x) => DraftHtml.HighlightOpen + x + DraftHtml.HighlightClose;

    private static readonly DateTime LocalNow = new(2026, 11, 13, 12, 0, 0);

    private const string Placeholder = "[[meeting_times]]";
    private static readonly string Body = "<p>Times:</p><p>" + H(Placeholder) + "</p><p>Best</p>";

    private const string MBlock =
        "1. Mon 11/16&nbsp; 09:00–10:00 PT<br>2. Mon 11/16&nbsp; 11:00–12:00 PT<br>3. Mon 11/16&nbsp; 08:00–09:00 PT";

    private static FakeFreeBusySource ScenarioM()
    {
        var f = new FakeFreeBusySource();
        f.Calendars["me@example.org"] = [(new DateTime(2026, 11, 16, 10, 0, 0), new DateTime(2026, 11, 16, 11, 0, 0), '2')];
        f.Free("ann@contoso.example");
        return f;
    }

    private static Settings Cfg(int options = 3)
    {
        var s = new Settings { TimeZoneLabel = "PT" };
        s.AiDrafting.MeetingTimeOptions = options;
        return s;
    }

    private static MeetingTimesRequest Req(string body, MeetingAsk? ask = null, bool europe = false, string own = "me@example.org",
        string sender = "ann@contoso.example", int options = 3) =>
        new(body, ask, own, "", sender, null, null, false, europe, DraftHarness.Now, Cfg(options));

    private static DraftMeetingTimes Make(IFreeBusySource? src, Func<TimeZoneInfo?>? cet = null) =>
        new(src, DraftHarness.Zone, cet ?? TestZones.Berlin);

    private static DraftReply Reply(MeetingAsk? meeting) => new(true, 0.9, null, "<p>x</p>", meeting);

    private static MeetingAsk Ask(int? dur = null, string? earliest = null, string? latest = null, string? heading = null) =>
        new(dur, earliest is null ? null : DateOnly.Parse(earliest), latest is null ? null : DateOnly.Parse(latest), heading);

    // ---------------------------------------------------------------- OtherAttendees

    [Fact]
    public void Others_sender_only()
    {
        Assert.Equal(["ann@x.org"], DraftMeetingTimes.OtherAttendees("ann@x.org", null, null, false, "me@y.org", ""));
    }

    [Fact]
    public void Others_reply_all()
    {
        var r = DraftMeetingTimes.OtherAttendees("ann@x.org", "Me <me@y.org>; Bob <bob@x.org>; Nobody", "carol@z.org; BOB@x.org; <bad>",
            true, "me@y.org", "");
        Assert.Equal(["ann@x.org", "bob@x.org", "carol@z.org"], r);
    }

    [Fact]
    public void Others_not_reply_all_is_sender_only()
    {
        var r = DraftMeetingTimes.OtherAttendees("ann@x.org", "Me <me@y.org>; Bob <bob@x.org>; Nobody", "carol@z.org; BOB@x.org; <bad>",
            false, "me@y.org", "");
        Assert.Equal(["ann@x.org"], r);
    }

    [Fact]
    public void Others_exclude_shared_mailbox_and_own_case_insensitively()
    {
        var r = DraftMeetingTimes.OtherAttendees("ann@x.org", "Help <help@y.org>; ME@Y.ORG; bob@x.org", null, true, "me@y.org", "help@y.org");
        Assert.Equal(["ann@x.org", "bob@x.org"], r);
    }

    [Fact]
    public void Others_sender_equal_to_own_is_left_out()
    {
        var r = DraftMeetingTimes.OtherAttendees("me@y.org", "bob@x.org", null, true, "me@y.org", "");
        Assert.Equal(["bob@x.org"], r);
    }

    [Fact]
    public void Others_invalid_sender_starts_with_first_valid_to()
    {
        var r = DraftMeetingTimes.OtherAttendees("/O=EXCHANGE/OU=X/CN=ANN", "bob@x.org; carol@x.org", null, true, "me@y.org", "");
        Assert.Equal(["bob@x.org", "carol@x.org"], r);
    }

    [Fact]
    public void Others_are_capped_at_ten_sender_first()
    {
        var to = string.Join("; ", Enumerable.Range(1, 12).Select(i => $"p{i}@x.org"));
        var r = DraftMeetingTimes.OtherAttendees("ann@x.org", to, null, true, "me@y.org", "");
        Assert.Equal(10, r.Count);
        Assert.Equal("ann@x.org", r[0]);
        Assert.Equal(Enumerable.Range(1, 9).Select(i => $"p{i}@x.org"), r.Skip(1));
    }

    [Fact]
    public void Others_all_null_is_empty()
    {
        Assert.Empty(DraftMeetingTimes.OtherAttendees(null, null, null, true, null, null));
    }

    // ---------------------------------------------------------------- Range

    [Fact]
    public void Range_default()
    {
        var r = DraftMeetingTimes.Range(LocalNow, null);
        Assert.Equal(new MeetingRange(new DateTime(2026, 11, 14), new DateTime(2026, 11, 24)), r);
        Assert.Equal(new DateOnly(2026, 11, 14), r.FirstDay);
        Assert.Equal(new DateOnly(2026, 11, 23), r.LastDay);
        Assert.False(r.IsEmpty);
    }

    [Theory]
    [InlineData("2026-11-20", null, "2026-11-20", "2026-11-30")]
    [InlineData("2026-11-10", null, "2026-11-14", "2026-11-24")]
    [InlineData(null, "2026-11-17", "2026-11-14", "2026-11-18")]
    [InlineData(null, "2026-11-12", "2026-11-14", "2026-11-24")]
    [InlineData("2026-11-20", "2026-11-18", "2026-11-20", "2026-11-30")]
    [InlineData("2026-11-20", "2026-11-25", "2026-11-20", "2026-11-26")]
    [InlineData(null, "2027-03-01", "2026-11-14", "2027-01-12")]
    public void Range_cases(string? earliest, string? latest, string from, string to)
    {
        var r = DraftMeetingTimes.Range(LocalNow, Ask(earliest: earliest, latest: latest));
        Assert.Equal(new MeetingRange(DateTime.Parse(from), DateTime.Parse(to)), r);
    }

    [Fact]
    public void Range_latest_17th_last_day()
    {
        Assert.Equal(new DateOnly(2026, 11, 17), DraftMeetingTimes.Range(LocalNow, Ask(latest: "2026-11-17")).LastDay);
    }

    [Fact]
    public void Range_earliest_beyond_cap_is_empty()
    {
        var r = DraftMeetingTimes.Range(LocalNow, Ask(earliest: "2027-02-01"));
        Assert.True(r.IsEmpty);
        Assert.Equal(new DateOnly(2027, 2, 1), r.FirstDay);
        Assert.Equal(new DateOnly(2027, 2, 1), r.LastDay);
    }

    [Fact]
    public void Range_survives_extreme_dates()
    {
        Assert.True(DraftMeetingTimes.Range(LocalNow, Ask(earliest: "9999-12-31", latest: "9999-12-31")).IsEmpty);
        var r = DraftMeetingTimes.Range(LocalNow, Ask(latest: "9999-12-31"));
        Assert.Equal(new DateTime(2027, 1, 12), r.To);
    }

    // ---------------------------------------------------------------- IsWanted

    [Fact]
    public void IsWanted_cases()
    {
        var on = new AiDraftingSettings { MeetingTimes = true };
        var off = new AiDraftingSettings { MeetingTimes = false };
        var meeting = Ask();
        Assert.True(DraftMeetingTimes.IsWanted(on, Reply(meeting), "<p>No placeholder</p>"));
        Assert.True(DraftMeetingTimes.IsWanted(on, Reply(null), "<p>" + H(Placeholder) + "</p>"));
        Assert.True(DraftMeetingTimes.IsWanted(on, Reply(null), "<p>[[Meeting-Times]]</p>"));
        Assert.False(DraftMeetingTimes.IsWanted(on, Reply(null), "<p>Nothing</p>"));
        Assert.False(DraftMeetingTimes.IsWanted(off, Reply(meeting), "<p>" + H(Placeholder) + "</p>"));
    }

    // ---------------------------------------------------------------- ApplyAsync

    [Theory]
    [InlineData(WorkPriority.Background)]
    [InlineData(WorkPriority.Interactive)]
    public async Task Scenario_M_is_offered(WorkPriority priority)
    {
        var f = ScenarioM();
        var r = await Make(f).ApplyAsync(Req(Body), priority, Ct);
        Assert.Equal(MeetingTimesOutcome.Offered, r.Outcome);
        Assert.Equal(3, r.Offered);
        Assert.Equal(2, r.Readable);
        Assert.Equal(2, r.Checked);
        Assert.False(r.CetMissing);
        Assert.Null(r.ErrorType);
        Assert.Equal("<p>Times:</p><p>" + MBlock + "</p><p>Best</p>", r.Html);
        Assert.Equal(["me@example.org", "ann@contoso.example"], f.Calls.Select(c => c.Email));
        Assert.All(f.Calls, c =>
        {
            Assert.Equal(new DateTime(2026, 11, 14), c.From);
            Assert.Equal(new DateTime(2026, 11, 24), c.To);
            Assert.Equal(30, c.Interval);
            Assert.Equal(priority, c.Priority);
        });
        Assert.Equal(0, f.CurrentUserCalls);
    }

    [Fact]
    public async Task Owner_missing_is_unreadable_and_only_the_owner_is_asked()
    {
        var f = new FakeFreeBusySource();
        f.Free("ann@contoso.example");
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r.Outcome);
        Assert.Equal(0, r.Offered);
        Assert.Equal(0, r.Readable);
        Assert.Equal(2, r.Checked);
        Assert.Contains(H("[[add meeting times: Clippy couldn't read your calendar]]"), r.Html);
        Assert.DoesNotContain("meeting_times", r.Html);
        Assert.Single(f.Calls);
    }

    [Theory]
    [InlineData("000")]
    public async Task Owner_short_string_is_unreadable(string raw)
    {
        var f = ScenarioM();
        f.Raw["me@example.org"] = raw;
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r.Outcome);
    }

    [Fact]
    public async Task Owner_string_with_bad_code_is_unreadable()
    {
        var f = ScenarioM();
        f.Raw["me@example.org"] = "x" + new string('0', 479);
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r.Outcome);
        Assert.Single(f.Calls);
    }

    [Fact]
    public async Task Owner_throws_gives_unreadable_with_type()
    {
        var f = ScenarioM();
        f.Throws["me@example.org"] = new InvalidOperationException("secret");
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r.Outcome);
        Assert.Equal("InvalidOperationException", r.ErrorType);
    }

    [Fact]
    public async Task Sender_throws_gives_unreadable()
    {
        var f = ScenarioM();
        f.Throws["ann@contoso.example"] = new InvalidOperationException();
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r.Outcome);
        Assert.Equal("InvalidOperationException", r.ErrorType);
    }

    [Fact]
    public async Task Outlook_gone_is_rethrown()
    {
        var f = ScenarioM();
        f.Throws["me@example.org"] = new OutlookUnavailableException();
        await Assert.ThrowsAsync<OutlookUnavailableException>(() => Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct));

        var g = ScenarioM();
        g.Throws["ann@contoso.example"] = new OutlookNotRespondingException();
        await Assert.ThrowsAsync<OutlookNotRespondingException>(() => Make(g).ApplyAsync(Req(Body), WorkPriority.Background, Ct));
    }

    [Fact]
    public async Task Unknown_sender_is_left_out()
    {
        var f = ScenarioM();
        f.Calendars.Remove("ann@contoso.example");
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.Offered, r.Outcome);
        Assert.Equal(1, r.Readable);
        Assert.Equal(2, r.Checked);
        Assert.Contains(MBlock, r.Html);
    }

    [Fact]
    public async Task Owner_busy_all_range_is_no_free_time()
    {
        var f = new FakeFreeBusySource();
        f.Calendars["me@example.org"] = [(new DateTime(2026, 11, 14), new DateTime(2026, 11, 24), '2')];
        f.Free("ann@contoso.example");
        var r = await Make(f).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.NoFreeTime, r.Outcome);
        Assert.Equal(0, r.Offered);
        Assert.Equal(2, r.Readable);
        Assert.Contains(H("[[add meeting times: no free time found between 2026-11-14 and 2026-11-23]]"), r.Html);
    }

    [Fact]
    public async Task Source_null_or_blank_owner_is_unreadable_without_calls()
    {
        var r = await Make(null).ApplyAsync(Req(Body), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r.Outcome);
        Assert.Equal(0, r.Readable);

        var f = ScenarioM();
        var r2 = await Make(f).ApplyAsync(Req(Body, own: ""), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.CalendarUnreadable, r2.Outcome);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task Empty_range_is_no_free_time_without_calls()
    {
        var f = ScenarioM();
        var r = await Make(f).ApplyAsync(Req(Body, Ask(earliest: "2027-02-01")), WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.NoFreeTime, r.Outcome);
        Assert.Contains("between 2027-02-01 and 2027-02-01", r.Html);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task Europe_splits_into_two_lists()
    {
        var f = ScenarioM();
        var r = await Make(f).ApplyAsync(
            Req(Body, Ask(latest: "2026-11-17", heading: "Außerhalb der europäischen Bürozeiten"), europe: true),
            WorkPriority.Background, Ct);
        Assert.Equal(MeetingTimesOutcome.Offered, r.Outcome);
        Assert.Contains(
            "1. Mon 11/16&nbsp; 08:00–09:00 PT&nbsp; (17:00–18:00 CET)<br>2. Tue 11/17&nbsp; 08:00–09:00 PT&nbsp; (17:00–18:00 CET)"
            + "<br><br>Außerhalb der europäischen Bürozeiten:<br>3. Mon 11/16&nbsp; 09:00–10:00 PT&nbsp; (18:00–19:00 CET)",
            r.Html);
    }

    [Fact]
    public async Task Europe_without_cet_zone_flags_cet_missing_and_only_asks_when_europe()
    {
        var f = ScenarioM();
        var r = await Make(f, () => null).ApplyAsync(Req(Body, europe: true), WorkPriority.Background, Ct);
        Assert.True(r.CetMissing);
        Assert.Contains(MBlock, r.Html);

        var calls = 0;
        var g = ScenarioM();
        var r2 = await Make(g, () => { calls++; return TestZones.Berlin(); }).ApplyAsync(Req(Body, europe: false), WorkPriority.Background, Ct);
        Assert.Equal(0, calls);
        Assert.False(r2.CetMissing);
        Assert.Equal(MeetingTimesOutcome.Offered, r2.Outcome);
    }

    [Fact]
    public async Task Duration_and_option_count_are_used()
    {
        var f = ScenarioM();
        var r = await Make(f).ApplyAsync(Req(Body, Ask(dur: 30), options: 2), WorkPriority.Background, Ct);
        Assert.Equal(2, r.Offered);
        Assert.Contains("1. Mon 11/16&nbsp; 09:30–10:00 PT<br>2. Mon 11/16&nbsp; 11:00–11:30 PT", r.Html);
    }

    [Fact]
    public async Task Without_placeholder_the_block_goes_in_a_new_paragraph()
    {
        var f = ScenarioM();
        var r = await Make(f).ApplyAsync(Req("<p>Sure.</p><p>Best</p>", Ask()), WorkPriority.Background, Ct);
        Assert.Equal("<p>Sure.</p><p>" + MBlock + "</p><p>Best</p>", r.Html);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        var f = ScenarioM();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make(f).ApplyAsync(Req(Body), WorkPriority.Background, cts.Token));

        var g = ScenarioM();
        using var cts2 = new CancellationTokenSource();
        g.OnCall = _ => cts2.Cancel();
        g.Throws["me@example.org"] = new OperationCanceledException(cts2.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Make(g).ApplyAsync(Req(Body), WorkPriority.Background, cts2.Token));
    }

    [Fact]
    public void Constructor_rejects_null_zone_and_cet()
    {
        Assert.Throws<ArgumentNullException>(() => new DraftMeetingTimes(null, null!, TestZones.Berlin));
        Assert.Throws<ArgumentNullException>(() => new DraftMeetingTimes(null, DraftHarness.Zone, null!));
    }

    [Fact]
    public async Task Request_null_throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Make(null).ApplyAsync(null!, WorkPriority.Background, Ct));
    }
}
