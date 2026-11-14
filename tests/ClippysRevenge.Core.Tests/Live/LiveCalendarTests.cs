using System.Globalization;
using System.Runtime.InteropServices;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// Unsent meeting draft, list_events windows, free/busy and find_free_times (SPEC §22.1, §11.7, §11.8).
/// </summary>
[Collection(LiveOutlookCollection.Name)]
[Trait("live", "outlook")]
[Trait("os", "windows")]
public sealed class LiveCalendarTests(LiveOutlook fx)
{
    private const string PidLidFInvited = "http://schemas.microsoft.com/mapi/id/{00062002-0000-0000-C000-000000000046}/8229000B";

    private sealed record Appointment(string EntryId, string StoreId, string Subject, DateTime Start, DateTime End,
        Outlook.OlMeetingStatus MeetingStatus, bool Invited);

    private static void Rel(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    /// <summary>Appointments of the own default calendar whose subject contains the run token.</summary>
    private List<Appointment> FindMeetings(Outlook.Application app) =>
        fx.OnFolder(app, "", Outlook.OlDefaultFolders.olFolderCalendar, folder =>
        {
            var found = new List<Appointment>();
            Outlook.Items? items = null, restricted = null;
            try
            {
                items = folder.Items;
                items.IncludeRecurrences = false;
                restricted = items.Restrict($"@SQL=\"urn:schemas:httpmail:subject\" LIKE '%{fx.RunToken}%'");
                object? o = restricted.GetFirst();
                while (o is not null)
                {
                    try
                    {
                        if (o is Outlook.AppointmentItem a)
                        {
                            var invited = false;
                            Outlook.PropertyAccessor? pa = null;
                            try
                            {
                                pa = a.PropertyAccessor;
                                invited = pa.GetProperty(PidLidFInvited) is true;
                            }
                            catch (COMException)
                            {
                                invited = false;   // a missing property counts as false
                            }
                            finally
                            {
                                Rel(pa);
                            }
                            found.Add(new Appointment(a.EntryID, folder.StoreID, a.Subject ?? "", a.Start, a.End,
                                a.MeetingStatus, invited));
                        }
                    }
                    finally
                    {
                        Rel(o);
                    }
                    o = restricted.GetNext();
                }
            }
            finally
            {
                Rel(restricted);
                Rel(items);
            }
            return found;
        });

    private static DateTimeOffset At(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, tz.GetUtcOffset(unspecified));
    }

    [Fact(Timeout = 180_000)]
    public async Task Meeting_draft_is_saved_unsent_and_listed()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var me = await fx.MeAsync();
        var ct = TestContext.Current.CancellationToken;
        var tz = TimeZoneInfo.Local;
        var s = DateTime.Today.AddDays(21).AddHours(5);
        var e = s.AddMinutes(30);
        var subject = $"[{fx.RunToken}] meeting";

        await fx.WithCleanupAsync([""], Outlook.OlDefaultFolders.olFolderCalendar, async scope =>
        {
            var invites = new MeetingInvites(new OutlookMeetingCalendar(fx.Worker, fx.Stores, () => fx.Settings), () => fx.Settings, fx.Hub, tz);

            var result = await invites.CreateDraftAsync(Recipients.Format([me]), subject,
                s.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), e.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                "Automated Clippy's Revenge live test. Never sent; deleted by the test.", null, null, ct);

            var found = await fx.RunAsync("finding the meeting", FindMeetings);
            foreach (var a in found)
                scope.Track(new TrackedItem(TrackedKind.Meeting, "", a.EntryId, a.StoreId, a.Subject));

            Assert.Equal("saved_as_draft", result.Status);

            var appt = Assert.Single(found);
            Assert.False(string.IsNullOrEmpty(result.EntryId), "The result has no EntryId.");
            Assert.Equal(appt.EntryId, result.EntryId);
            Assert.Equal(Outlook.OlMeetingStatus.olMeeting, appt.MeetingStatus);
            Assert.Equal(s, appt.Start);
            Assert.Equal(e, appt.End);
            Assert.False(appt.Invited, "PidLidFInvited is set: the meeting was invited.");
            var outbox = await fx.RunAsync("searching Outbox and Sent Items", app =>
                fx.TokenSubjectsIn(app, "", Outlook.OlDefaultFolders.olFolderOutbox)
                    .Concat(fx.TokenSubjectsIn(app, "", Outlook.OlDefaultFolders.olFolderSentMail)).ToList());
            Assert.Empty(outbox);

            async Task Check(string name, EventWindow window, bool expected, bool checkStart = false)
            {
                var events = await fx.RunAsync("list_events " + name, app => OutlookCalendar.ListEvents(app, fx.Stores, null, window, tz));
                var hit = events.FirstOrDefault(ev => ev.Subject.Contains(fx.RunToken, StringComparison.Ordinal));
                if ((hit is not null) != expected || (checkStart && hit is not null && hit.Start != At(s, tz)))
                    Assert.Fail($"list_events window looks shifted: event [{s:yyyy-MM-dd HH:mm}..{e:yyyy-MM-dd HH:mm}] vs window "
                        + $"[{window.Start:yyyy-MM-dd HH:mm zzz}..{window.End:yyyy-MM-dd HH:mm zzz}] ({name}, expected {(expected ? "found" : "absent")}), "
                        + $"UTC offset {tz.GetUtcOffset(s)}. Check the window bounds against the event times (CalendarEvents.DaslDate).");
            }

            await Check("[S-2h, S+2h]", new EventWindow(At(s.AddHours(-2), tz), At(s.AddHours(2), tz)), true, checkStart: true);
            await Check("[S+29m, S+90m]", new EventWindow(At(s.AddMinutes(29), tz), At(s.AddMinutes(90), tz)), true);
            await Check("[S+30m, S+90m]", new EventWindow(At(s.AddMinutes(30), tz), At(s.AddMinutes(90), tz)), false);
            await Check("[S-90m, S+1m]", new EventWindow(At(s.AddMinutes(-90), tz), At(s.AddMinutes(1), tz)), true);
            await Check("[S-90m, S]", new EventWindow(At(s.AddMinutes(-90), tz), At(s, tz)), false);
            await Check("the day of S", CalendarEvents.ResolveWindow(s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, 1, tz,
                DateTimeOffset.Now), true);

            fx.Result("dasl-calendar-window", tz.GetUtcOffset(s) == TimeSpan.Zero ? "inconclusive (UTC offset is 0)" : "ok");
        });
    }

    [Fact(Timeout = 180_000)]
    public async Task Free_busy_for_current_user()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var me = await fx.MeAsync();
        var ct = TestContext.Current.CancellationToken;
        var source = new OutlookFreeBusySource(fx.Worker);
        var today = DateTime.Today;
        foreach (var days in new[] { 14, 35 })
        {
            string? fb;
            try
            {
                fb = await source.GetFreeBusyAsync(me.Email, today, today.AddDays(days), 30, WorkPriority.Interactive, ct);
            }
            catch (OutlookNotRespondingException)
            {
                Assert.Skip(SkipTexts.NotResponding("free/busy", 30));
                return;
            }
            if (fb is null)
            {
                fx.Result("freebusy-self", "unknown (null)");
                continue;
            }
            // Outlook may return more than was asked for (whole 28-day chunks), never less.
            Assert.True(fb.Length >= days * 48, $"Free/busy for {days} days is {fb.Length} slots; expected at least {days * 48}.");
            Assert.All(fb, c => Assert.InRange(c, '0', '5'));
            fx.Result("freebusy-self", $"ok ({fb.Length} slots)");
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task Find_free_times_accepts_unknown()
    {
        await fx.RequireReadyAsync(TestContext.Current.CancellationToken);
        var me = await fx.MeAsync();
        var tz = TimeZoneInfo.Local;
        var finder = new FreeTimeFinder(new OutlookFreeBusySource(fx.Worker), TimeProvider.System, tz);
        FreeTimesResult result;
        try
        {
            result = await finder.FindAsync(new FreeTimeRequest(Recipients.Format([me]), 30, 5, MaxSuggestions: 3), fx.Settings,
                TestContext.Current.CancellationToken);
        }
        catch (OutlookNotRespondingException)
        {
            Assert.Skip(SkipTexts.NotResponding("find_free_times", 30));
            return;
        }

        Assert.True(result.Suggestions.Count <= 3);
        var floor = DateTimeOffset.Now.AddMinutes(-1);
        for (var i = 0; i < result.Suggestions.Count; i++)
        {
            var sg = result.Suggestions[i];
            Assert.Equal(i + 1, sg.N);
            var start = DateTimeOffset.Parse(sg.Start, CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse(sg.End, CultureInfo.InvariantCulture);
            Assert.True(start < end);
            Assert.True(end - start >= TimeSpan.FromMinutes(30), "A suggestion is shorter than 30 minutes.");
            Assert.True(start >= floor, "A suggestion starts in the past.");
        }
        if (result.UnknownAttendees.Count == 0)
            Assert.Contains(result.Attendees, a => string.Equals(a.Email, me.Email, StringComparison.OrdinalIgnoreCase));
        fx.Result("find-free-times", $"{result.Suggestions.Count} suggestions, unknown=[{string.Join(", ", result.UnknownAttendees)}]");
    }

    private static int TierRank(string adjacent) => adjacent switch { "busy" => 0, "tentative" => 1, _ => 2 };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [Fact(Timeout = 180_000)]
    public async Task Find_free_times_offers_match_line_format()
    {
        var ct = TestContext.Current.CancellationToken;
        await fx.RequireReadyAsync(ct);
        await fx.MeAsync();
        var inv = CultureInfo.InvariantCulture;
        var tz = TimeZoneInfo.Local;
        var cet = SenderRegion.CentralEurope();
        var settings = fx.Settings;
        var finder = new FreeTimeFinder(new OutlookFreeBusySource(fx.Worker), TimeProvider.System, tz);

        async Task<FreeTimesResult> Find(bool offerCet)
        {
            try
            {
                return await finder.FindAsync(
                    new FreeTimeRequest(null, DurationMinutes: 30, DaysAhead: 10, MaxSuggestions: 5, OfferCet: offerCet), settings, ct);
            }
            catch (OutlookNotRespondingException)
            {
                Assert.Skip(SkipTexts.NotResponding("find_free_times", 30));
                throw;
            }
        }

        var r = await Find(true);
        if (r.UnknownAttendees.Count > 0)
        {
            fx.Result("find-free-times-offers", "skipped: own free/busy unknown");
            Assert.Skip("Outlook returned no free/busy for your own calendar, so there are no times to offer. Nothing was created or sent.");
        }
        if (r.Offers.Count == 0)
        {
            Assert.True(r.OfferText == "", "There are no offers but the offer text is not empty.");
            Assert.True(r.Suggestions.Count == 0, "There are no offers but there are suggestions.");
            fx.Result("find-free-times-offers", "skipped: no free 30-minute time in 10 days");
            Assert.Skip("No free 30-minute time in the next 10 days; the offer format can't be checked.");
        }

        const string fmt = "yyyy-MM-dd'T'HH:mm:sszzz";
        Assert.True(r.Offers.Count is >= 1 and <= 5, "The number of offers is outside 1..5.");
        var parsed = new List<OfferLine>();
        var starts = new List<DateTimeOffset>();
        var ends = new List<DateTimeOffset>();
        var floor = DateTimeOffset.Now.AddMinutes(-1);
        var seenOutside = false;
        for (var i = 0; i < r.Offers.Count; i++)
        {
            var o = r.Offers[i];
            var p = LiveMeetingChecks.Parse(o.Label);
            Assert.True(o.N == i + 1, "The offers are not numbered 1, 2, 3 in order.");
            Assert.True(o.List is "main" or "outside_hours", "An offer is in an unknown list.");
            Assert.True(!(seenOutside && o.List == "main"), "A main offer follows an outside-hours offer.");
            seenOutside |= o.List == "outside_hours";
            Assert.True(o.Adjacent is "busy" or "tentative" or "none", "An offer has an unknown adjacency.");
            Assert.True(p is not null, "An offer line does not have the SPEC 21.5 format.");
            Assert.True(p.N == o.N, "The number in an offer line differs from its n.");
            var start = DateTimeOffset.ParseExact(o.Start, fmt, inv);
            var end = DateTimeOffset.ParseExact(o.End, fmt, inv);
            Assert.True(end - start == TimeSpan.FromMinutes(30), "An offer is not 30 minutes long.");
            Assert.True(start >= floor, "An offer starts in the past.");
            var ls = start.DateTime;
            var le = end.DateTime;
            Assert.True(p.Day == ls.ToString("ddd", inv) && p.Month == ls.Month && p.DayOfMonth == ls.Day
                && p.Start == TimeOnly.FromDateTime(ls) && p.End == TimeOnly.FromDateTime(le),
                "An offer line shows another day or time than its start and end.");
            Assert.True(p.Label == Blank(TimeZoneLabels.Resolve(settings.TimeZoneLabel, tz, ls)),
                "An offer line shows another time zone label than the settings give.");
            Assert.True(ls >= ls.Date.AddHours(settings.WorkStartHour) && le <= ls.Date.AddHours(settings.WorkEndHour),
                "An offer is outside the working hours.");
            Assert.True(!settings.BusinessDaysOnly || ls.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                "An offer is on a weekend.");
            parsed.Add(p);
            starts.Add(start);
            ends.Add(end);
        }

        for (var a = 0; a < starts.Count; a++)
            for (var b = a + 1; b < starts.Count; b++)
                Assert.True(!(starts[a] < ends[b] && starts[b] < ends[a]), "Two offers overlap.");

        foreach (var list in new[] { "main", "outside_hours" })
        {
            (int Tier, DateTimeOffset Start)? last = null;
            for (var i = 0; i < r.Offers.Count; i++)
            {
                if (r.Offers[i].List != list) continue;
                var key = (TierRank(r.Offers[i].Adjacent), starts[i]);
                if (last is { } l)
                    Assert.True(key.Item1 > l.Tier || (key.Item1 == l.Tier && key.Item2 >= l.Start),
                        "The offers of a list are not ranked by adjacency, then start.");
                last = key;
            }
        }

        var cetShown = parsed.Any(p => p.CetStart is not null);
        Assert.True(parsed.All(p => (p.CetStart is not null) == cetShown && (p.CetEnd is not null) == cetShown),
            "Only some offer lines have the CET part.");
        string cetState;
        if (cetShown)
        {
            Assert.True(cet is not null, "Offer lines have a CET part but this PC has no Central European zone.");
            for (var i = 0; i < parsed.Count; i++)
            {
                var cs = TimeZoneInfo.ConvertTime(starts[i], cet).DateTime;
                var ce = TimeZoneInfo.ConvertTime(ends[i], cet).DateTime;
                Assert.True(parsed[i].CetStart == TimeOnly.FromDateTime(cs) && parsed[i].CetEnd == TimeOnly.FromDateTime(ce),
                    "The CET part of an offer line is wrong.");
                var inHours = cs >= cs.Date.AddHours(MeetingOffers.EuropeStartHour)
                    && ce <= cs.Date.AddHours(MeetingOffers.EuropeEndHour);
                Assert.True(r.Offers[i].List == "main" ? inHours : !inHours,
                    "An offer is in the wrong list for European office hours.");
            }
            cetState = "yes";
        }
        else
        {
            Assert.True(r.Offers.All(o => o.List == "main"), "Offers are outside hours although no CET part is shown.");
            var sameOffset = cet is not null
                && (tz.GetUtcOffset(starts[0]) == cet.GetUtcOffset(starts[0])
                    || tz.GetUtcOffset(DateTimeOffset.Now) == cet.GetUtcOffset(DateTimeOffset.Now));
            Assert.True(cet is null || sameOffset, "No CET part is shown although this PC is not in CET.");
            cetState = cet is null ? "no (zone missing)" : "no (same offset)";
        }

        var main = r.Offers.Where(o => o.List == "main").Select(o => o.Label).ToList();
        var outside = r.Offers.Where(o => o.List == "outside_hours").Select(o => o.Label).ToList();
        var expected = string.Join("\n", main);
        if (outside.Count > 0)
            expected += (main.Count > 0 ? "\n\n" : "") + LiveMeetingChecks.DefaultHeading + "\n" + string.Join("\n", outside);
        Assert.True(string.Equals(r.OfferText, expected, StringComparison.Ordinal),
            "The offer text is not the offer lines with the outside-hours heading.");

        var plainCount = 0;
        var r2 = await Find(false);
        if (r2.Offers.Count > 0)
        {
            plainCount = r2.Offers.Count;
            var labels = r2.Offers.Select(o => o.Label).ToList();
            Assert.True(r2.Offers.All(o => o.List == "main"), "An offer without CET is not in the main list.");
            Assert.True(labels.All(l => LiveMeetingChecks.Parse(l) is { CetStart: null }),
                "An offer line without CET has no SPEC 21.5 format or has a CET part.");
            Assert.True(!r2.OfferText.Contains("CET", StringComparison.Ordinal)
                && !r2.OfferText.Contains(LiveMeetingChecks.DefaultHeading, StringComparison.Ordinal),
                "The offer text without CET mentions CET or the heading.");
            Assert.True(string.Equals(r2.OfferText, string.Join("\n", labels), StringComparison.Ordinal),
                "The offer text without CET is not the offer lines.");
        }

        fx.Result("find-free-times-offers",
            $"{r.Offers.Count} offers ({main.Count} main, {outside.Count} outside hours), cet={cetState}, plain={plainCount}");
    }
}
