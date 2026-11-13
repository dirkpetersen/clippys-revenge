using System.Text.Json;
using ModelContextProtocol;
using Xunit;

namespace ClippysRevenge.Core.Tests;

public class FreeTimeFinderTests
{
    private sealed class FixedTime(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class FakeSource(Dictionary<string, string?> map) : IFreeBusySource
    {
        public List<(string Email, DateTime From, DateTime To, int Interval)> Calls { get; } = [];
        public int UserCalls { get; private set; }
        public Exception? Throw { get; set; }

        public Task<Recipient> GetCurrentUserAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            UserCalls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new Recipient("Me", "me@x.org"));
        }

        public Task<string?> GetFreeBusyAsync(string email, DateTime from, DateTime to, int intervalMinutes, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((email, from, to, intervalMinutes));
            if (Throw is not null) throw Throw;
            return Task.FromResult(map.TryGetValue(email, out var v) ? v : null);
        }
    }

    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("Test-7", TimeSpan.FromHours(-7), "Test-7", "Test-7");

    private static DateTime L(int d, int h, int m = 0) => new(2026, 10, d, h, m, 0);

    private static string Fb(DateTime day0, int days, char code, params (DateTime S, DateTime E)[] busy)
    {
        var a = new char[days * 48];
        Array.Fill(a, '0');
        foreach (var (s, e) in busy)
            for (var t = s; t < e; t = t.AddMinutes(30))
                a[(int)((t - day0).TotalMinutes / 30)] = code;
        return new string(a);
    }

    private static Settings MakeSettings(Action<Settings>? tweak = null)
    {
        var s = Settings.CreateDefault("/p");
        s.WorkStartHour = 8;
        s.WorkEndHour = 17;
        s.BusinessDaysOnly = true;
        s.TimeZoneLabel = "PT";
        s.TentativeIsBusy = true;
        tweak?.Invoke(s);
        return s;
    }

    private static Dictionary<string, string?> Default(char bCode = '2') => new(StringComparer.OrdinalIgnoreCase)
    {
        ["a@x.org"] = Fb(L(12, 0), 1, '2', (L(12, 9), L(12, 10))),
        ["b@x.org"] = Fb(L(12, 0), 1, bCode, (L(12, 9, 30), L(12, 11)), (L(12, 12), L(12, 13))),
    };

    private static DateTimeOffset Utc(DateTime local) => new(local.AddHours(7), TimeSpan.Zero);

    private static (FreeTimeFinder F, FakeSource S) Make(Dictionary<string, string?>? map = null, DateTime? now = null, TimeZoneInfo? tz = null)
    {
        var src = new FakeSource(map ?? Default());
        var zone = tz ?? Tz;
        var n = now ?? L(12, 7);
        var utc = tz is null ? Utc(n).UtcDateTime : TimeZoneInfo.ConvertTimeToUtc(n, zone);
        return (new FreeTimeFinder(src, new FixedTime(new DateTimeOffset(utc, TimeSpan.Zero)), zone), src);
    }

    private static FreeTimeRequest Req(string? recipients = "A <a@x.org>; B <b@x.org>") =>
        new(recipients, IncludeMe: false, DaysAhead: 1);

    private static Task<FreeTimesResult> Run(FreeTimeFinder f, FreeTimeRequest r, Settings? s = null, CancellationToken? ct = null) =>
        f.FindAsync(r, s ?? MakeSettings(), ct ?? TestContext.Current.CancellationToken);

    private static readonly string[] Labels7 = ["1. Mon 10/12 8-9 PT", "2. Mon 10/12 11-12 PT", "3. Mon 10/12 1-5 PT"];

    [Fact]
    public async Task Basic()
    {
        var (f, src) = Make();
        var r = await Run(f, Req());
        Assert.Equal(Labels7, r.Suggestions.Select(s => s.Label));
        Assert.Equal("2026-10-12T08:00:00-07:00", r.Suggestions[0].Start);
        Assert.Equal("2026-10-12T09:00:00-07:00", r.Suggestions[0].End);
        Assert.Equal("2026-10-12T17:00:00-07:00", r.Suggestions[2].End);
        Assert.Equal([1, 2, 3], r.Suggestions.Select(s => s.N));
        Assert.Equal(string.Join("\n", Labels7), r.Text);
        Assert.Equal([new FreeTimeAttendee("A", "a@x.org"), new FreeTimeAttendee("B", "b@x.org")], r.Attendees);
        Assert.Empty(r.UnknownAttendees);
        Assert.Equal([("a@x.org", L(12, 7), L(13, 0), 30), ("b@x.org", L(12, 7), L(13, 0), 30)], src.Calls);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 1)]
    [InlineData(99, 3)]
    public async Task MaxSuggestions(int max, int expected)
    {
        var (f, _) = Make();
        var r = await Run(f, Req() with { MaxSuggestions = max });
        Assert.Equal(expected, r.Suggestions.Count);
        Assert.Equal(expected, r.Text.Split('\n').Length);
    }

    [Fact]
    public async Task Unknown_attendee_ignored()
    {
        var (f, _) = Make();
        var r = await Run(f, Req("A <a@x.org>; B <b@x.org>; C <c@x.org>"));
        Assert.Equal(["c@x.org"], r.UnknownAttendees);
        Assert.Equal(["a@x.org", "b@x.org"], r.Attendees.Select(a => a.Email));
        Assert.Equal(Labels7, r.Suggestions.Select(s => s.Label));
    }

    [Theory]
    [InlineData("02x")]
    [InlineData("")]
    [InlineData("short")]
    public async Task Invalid_or_short_is_unknown(string kind)
    {
        var map = Default();
        map["c@x.org"] = kind switch
        {
            "02x" => "02x" + new string('0', 45),
            "" => "",
            _ => new string('0', 47),
        };
        var (f, _) = Make(map);
        var r = await Run(f, Req("A <a@x.org>; B <b@x.org>; C <c@x.org>"));
        Assert.Equal(["c@x.org"], r.UnknownAttendees);
    }

    [Fact]
    public async Task All_unknown()
    {
        var (f, _) = Make(new Dictionary<string, string?>());
        var r = await Run(f, Req());
        Assert.Empty(r.Suggestions);
        Assert.Empty(r.Attendees);
        Assert.Equal(["a@x.org", "b@x.org"], r.UnknownAttendees);
        Assert.Equal("No free/busy information could be read for any attendee, so no times can be suggested. Check the addresses in unknown_attendees.", r.Text);
    }

    [Fact]
    public async Task IncludeMe()
    {
        var map = Default();
        map["me@x.org"] = Fb(L(12, 0), 1, '2', (L(12, 8), L(12, 9)));
        var (f, src) = Make(map);
        var r = await Run(f, Req() with { IncludeMe = true });
        Assert.Equal(1, src.UserCalls);
        Assert.Equal(["a@x.org", "b@x.org", "me@x.org"], r.Attendees.Select(a => a.Email));
        Assert.Equal(["1. Mon 10/12 11-12 PT", "2. Mon 10/12 1-5 PT"], r.Suggestions.Select(s => s.Label));
    }

    [Fact]
    public async Task IncludeMe_no_duplicate()
    {
        var map = Default();
        map["me@x.org"] = Fb(L(12, 0), 1, '2');
        var (f, src) = Make(map);
        var r = await Run(f, Req("A <a@x.org>; me <ME@x.org>") with { IncludeMe = true });
        Assert.Single(src.Calls, c => string.Equals(c.Email, "me@x.org", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ME@x.org", r.Attendees[1].Email);
        Assert.Equal(2, r.Attendees.Count);
    }

    private static Settings WithShared(bool enabled = true) =>
        MakeSettings(s => s.SharedMailboxes.Add(new SharedMailbox { Smtp = "help@x.org", DisplayName = "Helpdesk", Enabled = enabled }));

    [Fact]
    public async Task Shared_mailbox_owner()
    {
        var map = Default();
        map["help@x.org"] = Fb(L(12, 0), 1, '0');
        var (f, src) = Make(map);
        var r = await Run(f, Req() with { Mailbox = " Help@X.org ", IncludeMe = true }, WithShared());
        Assert.Equal(0, src.UserCalls);
        Assert.Contains(new FreeTimeAttendee("Helpdesk", "help@x.org"), r.Attendees);
        Assert.Contains(src.Calls, c => c.Email == "help@x.org");
    }

    [Theory]
    [InlineData("nobody@x.org", true, false)]
    [InlineData("help@x.org", false, false)]
    [InlineData("nobody@x.org", true, true)]
    public async Task Unknown_mailbox(string mailbox, bool enabled, bool includeMe)
    {
        var (f, src) = Make();
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            Run(f, Req() with { Mailbox = mailbox, IncludeMe = includeMe }, WithShared(enabled)));
        var avail = enabled ? "\"\" (your own mailbox), \"help@x.org\"" : "\"\" (your own mailbox)";
        Assert.Equal(
            $"Mailbox \"{mailbox}\" is not configured or not enabled. Available mailboxes: {avail}. To use another shared mailbox, ask the user to enable it in the Clippy tray menu under Shared mailboxes.",
            ex.Message);
        Assert.Empty(src.Calls);
        Assert.Equal(0, src.UserCalls);
    }

    [Fact]
    public async Task Mailbox_all()
    {
        var (f, _) = Make();
        var ex = await Assert.ThrowsAsync<McpException>(() => Run(f, Req() with { Mailbox = "all" }, WithShared()));
        Assert.Equal("Mailbox \"all\" only works with search tools. Use one mailbox: \"\" (your own mailbox), \"help@x.org\".", ex.Message);
    }

    [Fact]
    public async Task Bad_recipient()
    {
        var (f, src) = Make();
        var ex = await Assert.ThrowsAsync<McpException>(() => Run(f, Req("Smith, John")));
        Assert.Equal("Recipient \"Smith, John\" has no valid email address. Use \"Name <email>\" or a bare email, and separate recipients with \";\".", ex.Message);
        Assert.Empty(src.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task No_attendees(string? recipients)
    {
        var (f, _) = Make();
        var ex = await Assert.ThrowsAsync<McpException>(() => Run(f, Req(recipients)));
        Assert.Equal("find_free_times needs at least one attendee. Pass recipients as \"Name <email>; ...\" or set include_me=true.", ex.Message);

        var map = new Dictionary<string, string?> { ["me@x.org"] = Fb(L(12, 0), 1, '0') };
        var (f2, src) = Make(map);
        var r = await Run(f2, Req(recipients) with { IncludeMe = true });
        Assert.Equal(["me@x.org"], r.Attendees.Select(a => a.Email));
        Assert.Equal(1, src.UserCalls);
    }

    [Fact]
    public async Task Attendee_limit()
    {
        var (f, _) = Make();
        string Many(int n) => string.Join(";", Enumerable.Range(0, n).Select(i => $"u{i}@x.org"));
        var ex = await Assert.ThrowsAsync<McpException>(() => Run(f, Req(Many(51))));
        Assert.Equal("find_free_times takes at most 50 attendees; got 51.", ex.Message);
        var r = await Run(f, Req(Many(50)));
        Assert.Equal(50, r.UnknownAttendees.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task Duration_invalid(int d)
    {
        var (f, _) = Make();
        var ex = await Assert.ThrowsAsync<McpException>(() => Run(f, Req() with { DurationMinutes = d }));
        Assert.Equal("duration_minutes must be between 1 and 1440.", ex.Message);
    }

    [Fact]
    public async Task Duration_max_accepted()
    {
        var (f, _) = Make();
        var r = await Run(f, Req() with { DurationMinutes = 1440 });
        Assert.Empty(r.Suggestions);
        Assert.StartsWith("No free time of at least 1440 minutes", r.Text);
    }

    [Theory]
    [InlineData(24, null, "earliest_hour must be between 0 and 23.")]
    [InlineData(-1, null, "earliest_hour must be between 0 and 23.")]
    [InlineData(null, 0, "latest_hour must be between 1 and 24.")]
    [InlineData(null, 25, "latest_hour must be between 1 and 24.")]
    [InlineData(12, 12, "latest_hour must be later than earliest_hour.")]
    public async Task Hours_invalid(int? e, int? l, string msg)
    {
        var (f, _) = Make();
        var ex = await Assert.ThrowsAsync<McpException>(() => Run(f, Req() with { EarliestHour = e, LatestHour = l }));
        Assert.Equal(msg, ex.Message);
    }

    [Fact]
    public async Task Hours_from_settings_and_override()
    {
        var map = new Dictionary<string, string?> { ["a@x.org"] = Fb(L(12, 0), 1, '0') };
        var (f, _) = Make(map);
        var r = await Run(f, Req("A <a@x.org>"), MakeSettings(s => { s.WorkStartHour = 9; s.WorkEndHour = 12; }));
        Assert.Equal(["1. Mon 10/12 9-12 PT"], r.Suggestions.Select(s => s.Label));

        var (f2, _) = Make(map, L(12, 5));
        var r2 = await Run(f2, Req("A <a@x.org>") with { EarliestHour = 6, LatestHour = 20 });
        Assert.Equal(["1. Mon 10/12 6am-8pm PT"], r2.Suggestions.Select(s => s.Label));
    }

    [Fact]
    public async Task Weekends()
    {
        var map = new Dictionary<string, string?> { ["a@x.org"] = Fb(L(16, 0), 3, '0') };
        var (f, _) = Make(map, L(16, 7));
        var req = new FreeTimeRequest("A <a@x.org>", IncludeMe: false, DaysAhead: 3);
        var r = await Run(f, req);
        Assert.Single(r.Suggestions);
        var r2 = await Run(f, req with { BusinessDaysOnly = false });
        Assert.Equal(3, r2.Suggestions.Count);
        Assert.Contains("Sat 10/17", r2.Suggestions[1].Label);
        Assert.Contains("Sun 10/18", r2.Suggestions[2].Label);
    }

    [Theory]
    [InlineData(0, 13)]
    [InlineData(10, 22)]
    public async Task Clamps(int days, int toDay)
    {
        var (f, src) = Make();
        await Run(f, Req() with { DaysAhead = days });
        Assert.Equal(L(toDay, 0), src.Calls[0].To);
    }

    [Fact]
    public async Task Clamp_max_days()
    {
        var (f, src) = Make();
        await Run(f, Req() with { DaysAhead = 100 });
        Assert.Equal(L(12, 0).AddDays(60), src.Calls[0].To);
    }

    [Fact]
    public async Task DateFrom()
    {
        var map = new Dictionary<string, string?> { ["a@x.org"] = Fb(L(14, 0), 10, '0') };
        var (f, src) = Make(map);
        var r = await Run(f, new FreeTimeRequest("A <a@x.org>", IncludeMe: false, DateFrom: "2026-10-14"));
        Assert.Equal(L(14, 0), src.Calls[0].From);
        Assert.Equal(L(24, 0), src.Calls[0].To);
        Assert.Equal("1. Wed 10/14 8-5 PT", r.Suggestions[0].Label);

        await Run(f, new FreeTimeRequest("A <a@x.org>", IncludeMe: false, DateFrom: "2026-10-14T10:00:00Z"));
        Assert.Equal(L(14, 3), src.Calls[1].From);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            Run(f, new FreeTimeRequest("A <a@x.org>", IncludeMe: false, DateFrom: "garbage")));
        Assert.Equal("date_from \"garbage\" is not a valid date. Use an ISO date like 2026-03-01 or a date-time like 2026-03-01T14:30 (local time).", ex.Message);
    }

    [Fact]
    public async Task Now_cutoff()
    {
        var map = new Dictionary<string, string?> { ["a@x.org"] = Fb(L(12, 0), 1, '0') };
        var (f, _) = Make(map, L(12, 10, 7));
        var r = await Run(f, Req("A <a@x.org>"));
        Assert.Equal("1. Mon 10/12 10:30-5 PT", r.Suggestions[0].Label);
    }

    [Fact]
    public async Task Tentative()
    {
        var (f, _) = Make(Default('1'));
        var r = await Run(f, Req());
        Assert.Equal(Labels7, r.Suggestions.Select(s => s.Label));

        var r2 = await Run(f, Req(), MakeSettings(s => s.TentativeIsBusy = false));
        Assert.Equal(["1. Mon 10/12 8-9 PT", "2. Mon 10/12 10-5 PT"], r2.Suggestions.Select(s => s.Label));
    }

    [Fact]
    public async Task Past_range()
    {
        var map = new Dictionary<string, string?> { ["a@x.org"] = Fb(L(1, 0), 2, '0') };
        var (f, _) = Make(map);
        var r = await Run(f, new FreeTimeRequest("A <a@x.org>", IncludeMe: false, DateFrom: "2026-10-01", DaysAhead: 2));
        Assert.Empty(r.Suggestions);
        Assert.Equal("No free time of at least 60 minutes was found for everyone between 2026-10-01 and 2026-10-02. Try a larger days_ahead, a shorter duration_minutes, or a wider earliest_hour/latest_hour.", r.Text);
    }

    [Fact]
    public async Task Errors_propagate()
    {
        var (f, src) = Make();
        var ex = new McpException("Outlook is not running. Ask the user to start Outlook (tray → Start Outlook) and try again.");
        src.Throw = ex;
        var got = await Assert.ThrowsAsync<McpException>(() => Run(f, Req()));
        Assert.Same(ex, got);

        var (f2, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(f2, Req(), ct: cts.Token));
    }

    [Fact]
    public async Task Dst()
    {
        var la = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var day0 = new DateTime(2026, 10, 30);
        var map = new Dictionary<string, string?> { ["a@x.org"] = new string('0', 4 * 48) };
        var (f, _) = Make(map, new DateTime(2026, 10, 30, 7, 0, 0), la);
        var r = await Run(f,
            new FreeTimeRequest("A <a@x.org>", IncludeMe: false, DaysAhead: 4, BusinessDaysOnly: false, MaxSuggestions: 10),
            MakeSettings(s => s.TimeZoneLabel = ""));
        Assert.Equal(4, r.Suggestions.Count);
        Assert.Equal("2026-10-31T08:00:00-07:00", r.Suggestions.Single(s => s.Start.StartsWith("2026-10-31")).Start);
        Assert.Equal("2026-11-02T08:00:00-08:00", r.Suggestions.Single(s => s.Start.StartsWith("2026-11-02")).Start);
        Assert.All(r.Suggestions, s => Assert.EndsWith(" PT", s.Label));
        _ = day0;
    }

    [Fact]
    public async Task Json_shape()
    {
        var (f, _) = Make();
        var r = await Run(f, Req("A <a@x.org>; B <b@x.org>; C <c@x.org>"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r));
        var root = doc.RootElement;
        Assert.Equal(["suggestions", "text", "unknown_attendees", "attendees"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["n", "label", "start", "end"], root.GetProperty("suggestions")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["name", "email"], root.GetProperty("attendees")[0].EnumerateObject().Select(p => p.Name));
    }
}
