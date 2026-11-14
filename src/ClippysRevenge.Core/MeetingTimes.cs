using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>The owner's calendar as read from their free/busy string (SPEC 21.2). Local wall-clock times.</summary>
public sealed class OwnerCalendar
{
    private OwnerCalendar(bool isKnown, IReadOnlyList<TimeRange> busy, IReadOnlyList<TimeRange> tentative)
    {
        IsKnown = isKnown;
        Busy = busy;
        Tentative = tentative;
    }

    /// <summary>IsKnown = false, no runs. Every tier is None.</summary>
    public static OwnerCalendar Unknown { get; } = new(false, Array.Empty<TimeRange>(), Array.Empty<TimeRange>());

    public bool IsKnown { get; }

    /// <summary>Merged runs of code '2', in time order.</summary>
    public IReadOnlyList<TimeRange> Busy { get; }

    /// <summary>Merged runs of code '1', in time order.</summary>
    public IReadOnlyList<TimeRange> Tentative { get; }

    /// <summary>Slot i covers [start + i*interval, start + (i+1)*interval). '2' is Busy, '1' is Tentative,
    /// '0' '3' '4' '5' are neither. Null or any other character gives Unknown. Empty string is known with no runs.</summary>
    public static OwnerCalendar FromFreeBusy(string? fb, DateTime start, int intervalMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalMinutes, 0);
        if (fb is null) return Unknown;

        var busy = new List<TimeRange>();
        var tentative = new List<TimeRange>();
        int busyStart = -1, tentStart = -1;
        for (var i = 0; i < fb.Length; i++)
        {
            var c = fb[i];
            if (c is not ('0' or '1' or '2' or '3' or '4' or '5')) return Unknown;

            if (c == '2')
            {
                if (busyStart < 0) busyStart = i;
            }
            else if (busyStart >= 0)
            {
                busy.Add(Make(start, busyStart, i, intervalMinutes));
                busyStart = -1;
            }

            if (c == '1')
            {
                if (tentStart < 0) tentStart = i;
            }
            else if (tentStart >= 0)
            {
                tentative.Add(Make(start, tentStart, i, intervalMinutes));
                tentStart = -1;
            }
        }

        if (busyStart >= 0) busy.Add(Make(start, busyStart, fb.Length, intervalMinutes));
        if (tentStart >= 0) tentative.Add(Make(start, tentStart, fb.Length, intervalMinutes));
        return new OwnerCalendar(true, busy, tentative);
    }

    private static TimeRange Make(DateTime start, int from, int to, int interval) =>
        new(start.AddMinutes((double)from * interval), start.AddMinutes((double)to * interval));
}

public enum MeetingTier { Busy = 0, Tentative = 1, None = 2 }

public readonly record struct RankedRange(TimeRange Range, MeetingTier Tier);

public static class MeetingRanking
{
    public const int AdjacencyToleranceMinutes = 15;

    /// <summary>"busy" | "tentative" | "none" (the JSON value of `adjacent`, SPEC 11.8).</summary>
    public static string Name(MeetingTier tier) => tier switch
    {
        MeetingTier.Busy => "busy",
        MeetingTier.Tentative => "tentative",
        _ => "none",
    };

    /// <summary>SPEC 21.2 adjacency. Overlap is never adjacent.</summary>
    public static bool IsAdjacent(TimeRange r, TimeRange ownerInterval)
    {
        var tol = TimeSpan.FromMinutes(AdjacencyToleranceMinutes);
        var after = r.Start - ownerInterval.End;
        if (after >= TimeSpan.Zero && after <= tol && r.Start.Date == LastDay(ownerInterval)) return true;
        var before = ownerInterval.Start - r.End;
        return before >= TimeSpan.Zero && before <= tol && ownerInterval.Start.Date == LastDay(r);
    }

    private static DateTime LastDay(TimeRange x) => x.End.AddTicks(-1).Date;

    /// <summary>Busy when adjacent to any Busy run, else Tentative when adjacent to any Tentative run, else None.</summary>
    public static MeetingTier TierOf(TimeRange r, OwnerCalendar owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsKnown) return MeetingTier.None;
        foreach (var b in owner.Busy)
            if (IsAdjacent(r, b)) return MeetingTier.Busy;
        foreach (var t in owner.Tentative)
            if (IsAdjacent(r, t)) return MeetingTier.Tentative;
        return MeetingTier.None;
    }

    /// <summary>Each range with its tier, ordered by tier, then Start (stable).</summary>
    public static IReadOnlyList<RankedRange> Rank(IEnumerable<TimeRange> ranges, OwnerCalendar owner)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(owner);
        return ranges
            .Select(r => new RankedRange(r, TierOf(r, owner)))
            .OrderBy(x => x.Tier)
            .ThenBy(x => x.Range.Start)
            .ToList();
    }
}

public enum MeetingOfferList { Main, OutsideHours }

/// <param name="N">1-based number, continued across the two lists.</param>
/// <param name="Range">Local wall-clock range (the owner's zone).</param>
/// <param name="Tier">Adjacency tier of the range.</param>
/// <param name="List">Which list the option is in.</param>
/// <param name="Cet">The range in CET, or null when the CET part is not shown.</param>
/// <param name="Line">FormatLine output.</param>
public sealed record MeetingOffer(int N, TimeRange Range, MeetingTier Tier, MeetingOfferList List, TimeRange? Cet, string Line)
{
    /// <summary>"main" | "outside_hours" (SPEC 11.8 `list`).</summary>
    public string ListName => List == MeetingOfferList.Main ? "main" : "outside_hours";
}

public sealed record MeetingOfferSet(IReadOnlyList<MeetingOffer> Main, IReadOnlyList<MeetingOffer> OutsideHours,
    bool CetShown, bool CetMissing)
{
    public static MeetingOfferSet Empty { get; } =
        new(Array.Empty<MeetingOffer>(), Array.Empty<MeetingOffer>(), false, false);

    public int Count => Main.Count + OutsideHours.Count;

    /// <summary>Main then OutsideHours (numbering order).</summary>
    public IReadOnlyList<MeetingOffer> All => Main.Concat(OutsideHours).ToList();

    /// <summary>SPEC 21.5 text form. "" when Count == 0.</summary>
    public string ToText(string? outsideHoursHeading = null)
    {
        if (Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append(string.Join("\n", Main.Select(o => o.Line)));
        if (OutsideHours.Count > 0)
        {
            if (Main.Count > 0) sb.Append("\n\n");
            sb.Append(MeetingOffers.Heading(outsideHoursHeading)).Append('\n');
            sb.Append(string.Join("\n", OutsideHours.Select(o => o.Line)));
        }

        return sb.ToString();
    }
}

public static class MeetingOffers
{
    public const int CandidateStepMinutes = 30;
    public const int EuropeStartHour = 8;
    public const int EuropeEndHour = 18;
    public const int MaxHeadingLength = 80;
    public const string OutsideHoursHeading = "Outside European office hours:";
    public const string CetLabel = "CET";

    /// <summary>SPEC 21.3 candidates, ordered by Start, duplicates removed.</summary>
    public static IReadOnlyList<TimeRange> Candidates(IReadOnlyList<TimeRange> freeBlocks, int durationMinutes)
    {
        ArgumentNullException.ThrowIfNull(freeBlocks);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationMinutes, 1);

        var dur = TimeSpan.FromMinutes(durationMinutes);
        var step = TimeSpan.FromMinutes(CandidateStepMinutes);
        var set = new HashSet<TimeRange>();
        foreach (var b in freeBlocks)
        {
            if (b.Duration < dur) continue;
            for (var s = b.Start; s + dur <= b.End; s += step) set.Add(new TimeRange(s, s + dur));
            set.Add(new TimeRange(b.End - dur, b.End));
        }

        return set.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
    }

    /// <summary>SPEC 21.3 selection: rank (tier, start), take each candidate that overlaps neither an already
    /// taken one nor any range in alreadyTaken, until count are taken.</summary>
    public static IReadOnlyList<RankedRange> Select(IReadOnlyList<TimeRange> candidates, OwnerCalendar owner, int count,
        IReadOnlyList<TimeRange>? alreadyTaken = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var taken = new List<RankedRange>();
        foreach (var c in MeetingRanking.Rank(candidates, owner))
        {
            if (taken.Count >= count) break;
            if (taken.Any(t => Overlaps(t.Range, c.Range))) continue;
            if (alreadyTaken is not null && alreadyTaken.Any(t => Overlaps(t, c.Range))) continue;
            taken.Add(c);
        }

        return taken;
    }

    private static bool Overlaps(TimeRange a, TimeRange b) => a.Start < b.End && b.Start < a.End;

    /// <summary>The whole SPEC 21.3 + 21.4 pipeline.</summary>
    public static MeetingOfferSet Build(IReadOnlyList<TimeRange> freeBlocks, OwnerCalendar owner, int durationMinutes,
        int count, TimeZoneInfo localZone, string? labelOverride, bool europe, TimeZoneInfo? centralEurope)
    {
        ArgumentNullException.ThrowIfNull(freeBlocks);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(localZone);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationMinutes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var cetMissing = europe && centralEurope is null;
        var cands = Candidates(freeBlocks, durationMinutes);
        if (cands.Count == 0) return new MeetingOfferSet(Array.Empty<MeetingOffer>(), Array.Empty<MeetingOffer>(), false, cetMissing);

        var cetShown = false;
        if (europe && centralEurope is not null)
        {
            var t0 = cands[0].Start;
            var off0 = localZone.GetUtcOffset(t0);
            var cetOff = centralEurope.GetUtcOffset(new DateTimeOffset(t0, off0));
            cetShown = off0 != cetOff;
        }

        DateTime ToCet(DateTime x) =>
            TimeZoneInfo.ConvertTime(new DateTimeOffset(x, localZone.GetUtcOffset(x)), centralEurope!).DateTime;

        IReadOnlyList<RankedRange> main;
        IReadOnlyList<RankedRange> second = Array.Empty<RankedRange>();
        if (!cetShown)
        {
            main = Select(cands, owner, count);
        }
        else
        {
            var inHours = new List<TimeRange>();
            var outHours = new List<TimeRange>();
            foreach (var c in cands)
            {
                var cs = ToCet(c.Start);
                var ce = ToCet(c.End);
                if (cs >= cs.Date.AddHours(EuropeStartHour) && ce <= cs.Date.AddHours(EuropeEndHour)) inHours.Add(c);
                else outHours.Add(c);
            }

            main = Select(inHours, owner, count);
            if (main.Count < count)
                second = Select(outHours, owner, count - main.Count, main.Select(m => m.Range).ToList());
        }

        var mainOffers = new List<MeetingOffer>();
        var secondOffers = new List<MeetingOffer>();
        var n = 0;
        foreach (var (list, source, target) in new[]
                 {
                     (MeetingOfferList.Main, main, mainOffers),
                     (MeetingOfferList.OutsideHours, second, secondOffers),
                 })
        {
            foreach (var rr in source)
            {
                n++;
                var label = TimeZoneLabels.Resolve(labelOverride, localZone, rr.Range.Start);
                TimeRange? cet = cetShown ? new TimeRange(ToCet(rr.Range.Start), ToCet(rr.Range.End)) : null;
                target.Add(new MeetingOffer(n, rr.Range, rr.Tier, list, cet, FormatLine(n, rr.Range, label, cet)));
            }
        }

        return new MeetingOfferSet(mainOffers, secondOffers, cetShown, cetMissing);
    }

    /// <summary>SPEC 21.5 line. cet null gives no CET part.</summary>
    public static string FormatLine(int n, TimeRange range, string? localLabel, TimeRange? cet)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        if (range.End <= range.Start) throw new ArgumentException("Range end must be after its start.", nameof(range));
        if (cet is { } c && c.End <= c.Start) throw new ArgumentException("CET end must be after its start.", nameof(cet));

        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"{n}. {range.Start:ddd} {range.Start:M/d}  {range.Start:HH:mm}–{range.End:HH:mm}");
        if (!string.IsNullOrWhiteSpace(localLabel)) sb.Append(' ').Append(localLabel.Trim());
        if (cet is { } cc) sb.Append(ci, $"  ({cc.Start:HH:mm}–{cc.End:HH:mm} {CetLabel})");
        return sb.ToString();
    }

    /// <summary>SPEC 21.4 heading rule: the cleaned model heading, or OutsideHoursHeading when it's null/invalid.</summary>
    public static string Heading(string? modelHeading)
    {
        if (modelHeading is null) return OutsideHoursHeading;

        var sb = new StringBuilder(modelHeading.Length);
        foreach (var ch in modelHeading)
            if (!char.IsControl(ch)) sb.Append(ch);

        var collapsed = new StringBuilder(sb.Length);
        var inSpace = false;
        for (var i = 0; i < sb.Length; i++)
        {
            if (char.IsWhiteSpace(sb[i]))
            {
                if (!inSpace) collapsed.Append(' ');
                inSpace = true;
            }
            else
            {
                collapsed.Append(sb[i]);
                inSpace = false;
            }
        }

        var cleaned = collapsed.ToString().Replace("<", "").Replace(">", "").Replace("[", "").Replace("]", "").Trim();
        if (cleaned.Length < 1 || cleaned.Length > MaxHeadingLength || !cleaned.Any(char.IsLetter))
            return OutsideHoursHeading;
        return cleaned.EndsWith(':') ? cleaned : cleaned + ":";
    }
}

public static partial class MeetingTimesHtml
{
    public const string CalendarUnreadable = "[[add meeting times: Clippy couldn't read your calendar]]";

    [GeneratedRegex(
        @"(?:<span style=""background-color:#ffff00"">\[\[ *meeting[ _-]times *\]\]</span>)|(?:\[\[ *meeting[ _-]times *\]\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TagRegex();

    /// <summary>"[[add meeting times: no free time found between yyyy-MM-dd and yyyy-MM-dd]]" (invariant).</summary>
    public static string NoFreeTime(DateOnly first, DateOnly last) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[[add meeting times: no free time found between {first:yyyy-MM-dd} and {last:yyyy-MM-dd}]]");

    /// <summary>True when html contains a placeholder (bare or highlighted).</summary>
    public static bool HasPlaceholder(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        try
        {
            return PlaceholderRegex().IsMatch(html);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>SPEC 21.6 insertion of the offer lines into sanitized HTML.</summary>
    public static string Insert(string sanitizedHtml, MeetingOfferSet offers, string? outsideHoursHeading = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizedHtml);
        ArgumentNullException.ThrowIfNull(offers);
        if (offers.Count == 0) throw new ArgumentException("No offers to insert; use InsertPlaceholder.", nameof(offers));

        var block = new StringBuilder();
        block.Append(string.Join("<br>", offers.Main.Select(o => Esc(o.Line))));
        if (offers.OutsideHours.Count > 0)
        {
            if (offers.Main.Count > 0) block.Append("<br><br>");
            block.Append(Esc(MeetingOffers.Heading(outsideHoursHeading))).Append("<br>");
            block.Append(string.Join("<br>", offers.OutsideHours.Select(o => Esc(o.Line))));
        }

        return Place(sanitizedHtml, block.ToString());
    }

    /// <summary>"Nothing to offer": puts placeholderText, highlighted, where the offers would have gone.</summary>
    public static string InsertPlaceholder(string sanitizedHtml, string placeholderText)
    {
        ArgumentNullException.ThrowIfNull(sanitizedHtml);
        ArgumentNullException.ThrowIfNull(placeholderText);
        return Place(sanitizedHtml, DraftHtml.HighlightOpen + Esc(placeholderText) + DraftHtml.HighlightClose);
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("  ", "&nbsp; ");

    private static readonly string[] BeforeNoBreak = ["<p>", "<li>", "<blockquote>", "<br>"];
    private static readonly string[] AfterNoBreak = ["</p>", "</li>", "</blockquote>", "<br>"];

    private static string Place(string html, string block)
    {
        Match m;
        try
        {
            m = PlaceholderRegex().Match(html);
        }
        catch (RegexMatchTimeoutException)
        {
            m = Match.Empty;
        }

        if (m.Success)
        {
            var before = html[..m.Index];
            var after = html[(m.Index + m.Length)..];
            var bt = before.TrimEnd();
            var prefix = bt.Length == 0 || BeforeNoBreak.Any(t => bt.EndsWith(t, StringComparison.OrdinalIgnoreCase))
                ? "" : "<br>";
            var at = after.TrimStart();
            var suffix = at.Length == 0 || AfterNoBreak.Any(t => at.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                ? "" : "<br>";
            return before + prefix + block + suffix + after;
        }

        var depth = 0;
        var pStarts = new List<int>();
        try
        {
            foreach (Match t in TagRegex().Matches(html))
            {
                var isEnd = t.Groups[1].Length > 0;
                var name = t.Groups[2].Value;
                if (name.Equals("br", StringComparison.OrdinalIgnoreCase) || t.Value.EndsWith("/>", StringComparison.Ordinal))
                    continue;
                if (isEnd)
                {
                    if (depth > 0) depth--;
                }
                else
                {
                    if (depth == 0 && name.Equals("p", StringComparison.OrdinalIgnoreCase)) pStarts.Add(t.Index);
                    depth++;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            pStarts.Clear();
        }

        var wrapped = "<p>" + block + "</p>";
        return pStarts.Count >= 2 ? html.Insert(pStarts[^1], wrapped) : html + wrapped;
    }
}
