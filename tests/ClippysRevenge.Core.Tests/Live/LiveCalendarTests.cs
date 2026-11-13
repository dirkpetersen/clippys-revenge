using System.Globalization;
using System.Runtime.InteropServices;
using ClippysRevenge.App;
using ClippysRevenge.Core;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>
/// DraftOnly meeting, list_events windows, free/busy and find_free_times (SPEC §6.6, §11.7 to §11.9).
/// The meeting only ever goes through DraftOnlyCalendar, which refuses anything but SaveDraft.
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
    public async Task Meeting_draft_only_is_saved_not_sent_and_listed()
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
            var calendar = new DraftOnlyCalendar(new OutlookMeetingCalendar(fx.Worker, fx.Stores, () => fx.Settings));
            var invites = new MeetingInvites(calendar, new RefusingConfirmer(), () => fx.Settings, fx.Hub, new BalloonCenter(), tz);

            var sendResult = await invites.SendAsync(Recipients.Format([me]), subject,
                s.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture), e.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                "Automated Clippy's Revenge live test. Never sent; deleted by the test.", null, false, null, ct);

            var found = await fx.RunAsync("finding the meeting", FindMeetings);
            foreach (var a in found)
                scope.Track(new TrackedItem(TrackedKind.Meeting, "", a.EntryId, a.StoreId, a.Subject));

            Assert.Equal("saved_as_draft", sendResult.Status);
            Assert.Equal("draft_only_mode", sendResult.Reason);
            Assert.Equal(InviteAction.SaveDraft, calendar.LastDecision?.Action);

            var appt = Assert.Single(found);
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
                        + $"UTC offset {tz.GetUtcOffset(s)}. Outlook may compare calendar DASL dates in UTC (CalendarEvents.DaslDate builds local time).");
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
                fb = await source.GetFreeBusyAsync(me.Email, today, today.AddDays(days), 30, ct);
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
            Assert.All(fb, c => Assert.InRange(c, '0', '4'));
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
}
