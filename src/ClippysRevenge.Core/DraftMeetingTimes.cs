namespace ClippysRevenge.Core;

public enum MeetingTimesOutcome { Offered, NoFreeTime, CalendarUnreadable }

/// <summary>Local wall-clock [From, To), both at local midnight. Empty when To &lt;= From.</summary>
public readonly record struct MeetingRange(DateTime From, DateTime To)
{
    public bool IsEmpty => To <= From;
    public DateOnly FirstDay => DateOnly.FromDateTime(From);

    /// <summary>The last day in the range; FirstDay when the range is empty.</summary>
    public DateOnly LastDay => IsEmpty ? FirstDay : DateOnly.FromDateTime(To.AddDays(-1));
}

/// <param name="SanitizedHtml">DraftHtml.Sanitize output (non-empty).</param>
/// <param name="Ask">DraftReply.Meeting (null when the model didn't set it but wrote the placeholder).</param>
/// <param name="OwnSmtp">The own mailbox's SMTP (the owner, also for shared-mailbox drafts). Blank = unknown.</param>
/// <param name="MailboxKey">"" for the own mailbox, else the shared mailbox's normalized SMTP.</param>
/// <param name="SenderEmail">The email's sender address (index / IndexedEmail.SenderEmail).</param>
/// <param name="To">Stored To list ("Name &lt;smtp&gt;; ...").</param>
/// <param name="Cc">Stored CC list.</param>
/// <param name="ReplyAll">reply.ReplyAll ?? false.</param>
/// <param name="Europe">SenderRegion.IsEurope result, computed by the caller.</param>
/// <param name="NowUtc">The worker's clock.</param>
/// <param name="Settings">The settings snapshot of this candidate.</param>
public sealed record MeetingTimesRequest(
    string SanitizedHtml, MeetingAsk? Ask, string OwnSmtp, string MailboxKey,
    string? SenderEmail, string? To, string? Cc, bool ReplyAll, bool Europe,
    DateTimeOffset NowUtc, Settings Settings);

/// <param name="Html">The sanitized HTML with the offers or a failure placeholder inserted.</param>
/// <param name="Offered">Number of options inserted (0 unless Outcome is Offered).</param>
/// <param name="Readable">Attendees (owner included) whose free/busy was read and usable.</param>
/// <param name="Checked">Attendees (owner included) that were to be checked: 1 + OtherAttendees.Count.</param>
/// <param name="CetMissing">MeetingOfferSet.CetMissing (Europe, but no CET zone on this PC).</param>
/// <param name="ErrorType">Exception type name when a non-Outlook-gone error gave CalendarUnreadable, else null.</param>
public sealed record MeetingTimesResult(string Html, MeetingTimesOutcome Outcome, int Offered, int Readable,
    int Checked, bool CetMissing, string? ErrorType);

/// <summary>SPEC 21.6: picks attendees and range, reads free/busy, builds and inserts the offers.
/// Logs nothing (the caller logs from the result).</summary>
public sealed class DraftMeetingTimes
{
    public const int MaxOtherAttendees = 10;
    public const int DefaultRangeDays = 10;
    public const int MaxRangeDays = 60;
    public const int IntervalMinutes = FreeBusy.DefaultIntervalMinutes;

    private readonly IFreeBusySource? _source;
    private readonly TimeZoneInfo _zone;
    private readonly Func<TimeZoneInfo?> _centralEurope;

    /// <param name="source">Null = no free/busy available (every ApplyAsync gives CalendarUnreadable, no calls).</param>
    /// <exception cref="ArgumentNullException">zone or centralEurope is null.</exception>
    public DraftMeetingTimes(IFreeBusySource? source, TimeZoneInfo zone, Func<TimeZoneInfo?> centralEurope)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(centralEurope);
        _source = source;
        _zone = zone;
        _centralEurope = centralEurope;
    }

    /// <summary>ai.MeetingTimes &amp;&amp; (reply.Meeting is not null || MeetingTimesHtml.HasPlaceholder(sanitizedHtml)).</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool IsWanted(AiDraftingSettings ai, DraftReply reply, string sanitizedHtml)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(sanitizedHtml);
        return ai.MeetingTimes && (reply.Meeting is not null || MeetingTimesHtml.HasPlaceholder(sanitizedHtml));
    }

    /// <summary>The others to check, sender first; never the owner or the shared mailbox. Pure, never throws for odd data.</summary>
    public static IReadOnlyList<string> OtherAttendees(string? senderEmail, string? to, string? cc, bool replyAll,
        string? ownSmtp, string? mailboxKey)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(ownSmtp)) skip.Add(ownSmtp.Trim());
        if (!string.IsNullOrWhiteSpace(mailboxKey)) skip.Add(mailboxKey.Trim());

        var result = new List<string>();

        void Add(string? entry)
        {
            if (result.Count >= MaxOtherAttendees) return;
            if (!Recipients.TryParseEntry(entry, out var r)) return;
            var email = r.Email.Trim();
            if (skip.Add(email)) result.Add(email);
        }

        Add(senderEmail);
        if (replyAll)
        {
            foreach (var list in new[] { to, cc })
            {
                if (string.IsNullOrEmpty(list)) continue;
                foreach (var entry in list.Split(';')) Add(entry);
            }
        }

        return result;
    }

    /// <summary>SPEC 21.6 "Range". localNow is the local wall-clock now.</summary>
    public static MeetingRange Range(DateTime localNow, MeetingAsk? ask)
    {
        var today = DateOnly.FromDateTime(localNow);
        var cap = today.AddDays(MaxRangeDays);
        var start = today.AddDays(1);
        if (ask?.EarliestDate is { } earliest && earliest > start) start = earliest;

        DateOnly end;
        if (start >= cap)
        {
            end = cap;
        }
        else if (ask?.LatestDate is { } latest && latest >= start)
        {
            end = latest >= cap ? cap : latest.AddDays(1);
        }
        else
        {
            end = start.AddDays(DefaultRangeDays);
        }

        if (end > cap) end = cap;
        return new MeetingRange(start.ToDateTime(TimeOnly.MinValue), end.ToDateTime(TimeOnly.MinValue));
    }

    /// <exception cref="ArgumentNullException">request is null.</exception>
    /// <exception cref="OutlookUnavailableException">From the source (incl. OutlookNotRespondingException); never caught here.</exception>
    /// <exception cref="OperationCanceledException">ct was cancelled.</exception>
    public async Task<MeetingTimesResult> ApplyAsync(MeetingTimesRequest request, WorkPriority priority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var localNow = TimeZoneInfo.ConvertTime(request.NowUtc, _zone).DateTime;
        var range = Range(localNow, request.Ask);
        var others = OtherAttendees(request.SenderEmail, request.To, request.Cc, request.ReplyAll, request.OwnSmtp,
            request.MailboxKey);
        var checkedCount = 1 + others.Count;
        var duration = request.Ask?.DurationMinutes ?? MeetingAsk.DefaultDurationMinutes;
        var settings = request.Settings;
        var count = Math.Clamp(settings.AiDrafting.MeetingTimeOptions, 1, 10);

        MeetingTimesResult Unreadable(string? errorType) =>
            new(MeetingTimesHtml.InsertPlaceholder(request.SanitizedHtml, MeetingTimesHtml.CalendarUnreadable),
                MeetingTimesOutcome.CalendarUnreadable, 0, 0, checkedCount, false, errorType);

        MeetingTimesResult NoFree(int readable, bool cetMissing) =>
            new(MeetingTimesHtml.InsertPlaceholder(request.SanitizedHtml,
                    MeetingTimesHtml.NoFreeTime(range.FirstDay, range.LastDay)),
                MeetingTimesOutcome.NoFreeTime, 0, readable, checkedCount, cetMissing, null);

        if (_source is null || string.IsNullOrWhiteSpace(request.OwnSmtp)) return Unreadable(null);
        if (range.IsEmpty) return NoFree(0, false);

        try
        {
            ct.ThrowIfCancellationRequested();
            var needed = (int)Math.Ceiling((range.To - range.From).TotalMinutes / IntervalMinutes);

            var fb = await _source.GetFreeBusyAsync(request.OwnSmtp.Trim(), range.From, range.To, IntervalMinutes, priority, ct)
                .ConfigureAwait(false);
            if (!Usable(fb, needed)) return Unreadable(null);
            IReadOnlyList<TimeRange> ownerBusy;
            try
            {
                ownerBusy = FreeBusy.BusyIntervals(fb!, range.From, IntervalMinutes, settings.TentativeIsBusy);
            }
            catch (FormatException)
            {
                return Unreadable(null);
            }
            var owner = OwnerCalendar.FromFreeBusy(fb, range.From, IntervalMinutes);

            var busyLists = new List<IReadOnlyList<TimeRange>> { ownerBusy };
            foreach (var email in others)
            {
                var ofb = await _source.GetFreeBusyAsync(email, range.From, range.To, IntervalMinutes, priority, ct)
                    .ConfigureAwait(false);
                if (!Usable(ofb, needed)) continue;
                try
                {
                    busyLists.Add(FreeBusy.BusyIntervals(ofb!, range.From, IntervalMinutes, settings.TentativeIsBusy));
                }
                catch (FormatException)
                {
                }
            }

            var readable = busyLists.Count;
            var blocks = FreeBusy.FreeSlots(busyLists, range.From, range.To, settings.WorkStartHour, settings.WorkEndHour,
                settings.BusinessDaysOnly, duration, localNow, IntervalMinutes);
            if (blocks.Count == 0) return NoFree(readable, false);

            var cet = request.Europe ? _centralEurope() : null;
            var set = MeetingOffers.Build(blocks, owner, duration, count, _zone, settings.TimeZoneLabel, request.Europe, cet);
            if (set.Count == 0) return NoFree(readable, set.CetMissing);

            var html = MeetingTimesHtml.Insert(request.SanitizedHtml, set, request.Ask?.OutsideHoursHeading);
            return new MeetingTimesResult(html, MeetingTimesOutcome.Offered, set.Count, readable, checkedCount,
                set.CetMissing, null);
        }
        catch (OutlookUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unreadable(ex.GetType().Name);
        }
    }

    private static bool Usable(string? fb, int needed) => !string.IsNullOrEmpty(fb) && fb.Length >= needed;
}
