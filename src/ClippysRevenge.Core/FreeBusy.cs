namespace ClippysRevenge.Core;

/// <summary>Half-open [Start, End) in local wall-clock time.</summary>
public readonly record struct TimeRange(DateTime Start, DateTime End)
{
    public TimeSpan Duration => End - Start;
}

public static class FreeBusy
{
    public const int ChunkDays = 28;
    public const int DefaultIntervalMinutes = 30;

    public static IReadOnlyList<TimeRange> BusyIntervals(
        string fb, DateTime start, int intervalMinutes, bool tentativeIsBusy)
    {
        ArgumentNullException.ThrowIfNull(fb);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalMinutes, 0);

        var result = new List<TimeRange>();
        var runStart = -1;
        for (var i = 0; i < fb.Length; i++)
        {
            var c = fb[i];
            bool busy;
            switch (c)
            {
                case '0':
                case '4':
                    busy = false;
                    break;
                case '1':
                    busy = tentativeIsBusy;
                    break;
                case '2':
                case '3':
                    busy = true;
                    break;
                default:
                    throw new FormatException($"Unexpected free/busy code '{c}' at position {i}.");
            }

            if (busy)
            {
                if (runStart < 0) runStart = i;
            }
            else if (runStart >= 0)
            {
                result.Add(Make(start, runStart, i, intervalMinutes));
                runStart = -1;
            }
        }

        if (runStart >= 0) result.Add(Make(start, runStart, fb.Length, intervalMinutes));
        return result;
    }

    private static TimeRange Make(DateTime start, int from, int to, int interval) =>
        new(start.AddMinutes((double)from * interval), start.AddMinutes((double)to * interval));

    public static IReadOnlyList<TimeRange> FreeSlots(
        IEnumerable<IReadOnlyList<TimeRange>> busyPerAttendee,
        DateTime from, DateTime to,
        int startHour, int endHour,
        bool businessDaysOnly,
        int minMinutes,
        DateTime now,
        int intervalMinutes = DefaultIntervalMinutes)
    {
        if (startHour < 0 || startHour >= 24) throw new ArgumentOutOfRangeException(nameof(startHour));
        if (endHour <= startHour || endHour > 24) throw new ArgumentOutOfRangeException(nameof(endHour));
        if (minMinutes < 1) throw new ArgumentOutOfRangeException(nameof(minMinutes));
        if (intervalMinutes < 1) throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        ArgumentNullException.ThrowIfNull(busyPerAttendee);

        var all = new List<TimeRange>();
        foreach (var list in busyPerAttendee)
        {
            if (list is null) throw new ArgumentNullException(nameof(busyPerAttendee), "Attendee busy list is null.");
            all.AddRange(list);
        }

        if (from >= to) return Array.Empty<TimeRange>();

        all.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<TimeRange>();
        foreach (var r in all)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                if (r.End > merged[^1].End) merged[^1] = new TimeRange(merged[^1].Start, r.End);
            }
            else
            {
                merged.Add(r);
            }
        }

        var step = TimeSpan.FromMinutes(intervalMinutes).Ticks;
        var sinceMidnight = (now - now.Date).Ticks;
        var cutoff = now.Date.AddTicks((sinceMidnight + step - 1) / step * step);
        var lower = from > cutoff ? from : cutoff;

        var result = new List<TimeRange>();
        var last = (to - TimeSpan.FromTicks(1)).Date;
        var minSpan = TimeSpan.FromMinutes(minMinutes);
        for (var d = from.Date; d <= last; d = d.AddDays(1))
        {
            if (businessDaysOnly && (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)) continue;

            var ws = d.AddHours(startHour);
            var we = d.AddHours(endHour);
            if (ws < lower) ws = lower;
            if (we > to) we = to;
            if (ws >= we) continue;

            var cursor = ws;
            foreach (var b in merged)
            {
                if (b.End <= cursor) continue;
                if (b.Start >= we) break;
                if (b.Start > cursor && b.Start - cursor >= minSpan)
                    result.Add(new TimeRange(cursor, b.Start));
                cursor = b.End;
            }

            if (cursor < we && we - cursor >= minSpan) result.Add(new TimeRange(cursor, we));
        }

        return result;
    }

    public static IReadOnlyList<DateTime> ChunkStarts(DateTime from, DateTime to)
    {
        var result = new List<DateTime>();
        if (to <= from) return result;
        for (var s = from.Date; s < to; s = s.AddDays(ChunkDays)) result.Add(s);
        return result;
    }

    /// <summary>Fetches [midnight of from, to) as 28-day chunks and joins them. Null when any chunk is null or a non-last chunk is short.</summary>
    public static string? FetchChunked(DateTime from, DateTime to, int intervalMinutes, Func<DateTime, string?> fetchChunk)
    {
        if (intervalMinutes <= 0 || 1440 % intervalMinutes != 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        ArgumentNullException.ThrowIfNull(fetchChunk);

        var starts = ChunkStarts(from, to);
        if (starts.Count == 0) return "";
        var chunks = new List<string>(starts.Count);
        foreach (var s in starts)
        {
            var c = fetchChunk(s);
            if (c is null) return null;
            chunks.Add(c);
        }

        try
        {
            return JoinChunks(chunks, intervalMinutes);
        }
        catch (ArgumentException ex) when (ex.GetType() == typeof(ArgumentException))
        {
            return null;
        }
    }

    public static string JoinChunks(IReadOnlyList<string> chunks, int intervalMinutes)
    {
        if (intervalMinutes <= 0 || 1440 % intervalMinutes != 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes));
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var c in chunks) ArgumentNullException.ThrowIfNull(c, nameof(chunks));

        var slots = ChunkDays * 1440 / intervalMinutes;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (i == chunks.Count - 1)
            {
                sb.Append(c);
            }
            else
            {
                if (c.Length < slots)
                    throw new ArgumentException(
                        $"Free/busy chunk {i} has {c.Length} slots; expected at least {slots}.", nameof(chunks));
                sb.Append(c, 0, slots);
            }
        }

        return sb.ToString();
    }
}
