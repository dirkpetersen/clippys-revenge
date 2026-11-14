using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClippysRevenge.Core;

namespace ClippysRevenge.Core.Tests.Live;

/// <summary>One SPEC 21.5 option line, parsed. Label null when the line has none; Cet* null without the CET part.</summary>
internal sealed record OfferLine(int N, string Day, int Month, int DayOfMonth, TimeOnly Start, TimeOnly End,
    string? Label, TimeOnly? CetStart, TimeOnly? CetEnd);

/// <summary>Pure checks for the live meeting-times tests (M13.7). Nothing here prints mail text or times.</summary>
internal static partial class LiveMeetingChecks
{
    /// <summary>The SPEC 21.4 default heading (MeetingOffers.OutsideHoursHeading), for readability.</summary>
    public const string DefaultHeading = MeetingOffers.OutsideHoursHeading;

    [GeneratedRegex(
        @"^(?<n>[1-9][0-9]?)\. (?<ddd>Mon|Tue|Wed|Thu|Fri|Sat|Sun) (?<m>1[0-2]|[1-9])/(?<d>3[01]|[12][0-9]|[1-9])  "
        + @"(?<s>(?:[01][0-9]|2[0-3]):[0-5][0-9])–(?<e>(?:[01][0-9]|2[0-3]):[0-5][0-9])"
        + @"(?: (?<label>[^\s()]+(?: [^\s()]+)*))?"
        + @"(?:  \((?<cs>(?:[01][0-9]|2[0-3]):[0-5][0-9])–(?<ce>(?:[01][0-9]|2[0-3]):[0-5][0-9]) CET\))?\z",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LineRx();

    [GeneratedRegex(@"<\s*/?\s*(?:br|p|li|blockquote)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BreakTagRx();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AnyTagRx();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SpaceRunRx();

    /// <summary>Parses exactly one SPEC 21.5 line (no trimming). Null when the line doesn't have that shape. Never throws.</summary>
    public static OfferLine? Parse(string? line)
    {
        if (line is null) return null;
        try
        {
            var m = LineRx().Match(line);
            if (!m.Success) return null;
            var ci = CultureInfo.InvariantCulture;
            TimeOnly T(string name) => TimeOnly.ParseExact(m.Groups[name].Value, "HH:mm", ci);
            var hasCet = m.Groups["cs"].Success;
            return new OfferLine(
                int.Parse(m.Groups["n"].Value, ci), m.Groups["ddd"].Value,
                int.Parse(m.Groups["m"].Value, ci), int.Parse(m.Groups["d"].Value, ci),
                T("s"), T("e"),
                m.Groups["label"].Success ? m.Groups["label"].Value : null,
                hasCet ? T("cs") : null, hasCet ? T("ce") : null);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Text lines of an HTML fragment WITHOUT collapsing spaces: br/p/li/blockquote tags (open or close,
    /// any case, optional "/") become line breaks, other tags are removed, entities decoded, U+00A0 becomes ' ',
    /// each line trimmed, empty lines dropped. Null gives an empty list. Never throws.</summary>
    public static IReadOnlyList<string> HtmlLines(string? html)
    {
        if (string.IsNullOrEmpty(html)) return [];
        try
        {
            var text = BreakTagRx().Replace(html, "\n");
            text = AnyTagRx().Replace(text, "");
            text = WebUtility.HtmlDecode(text).Replace(' ', ' ').Replace("\r", "");
            return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    /// <summary>For comparing with Outlook's plain Body: U+00A0 → ' ', "\r" removed, runs of ' '/'\t' → one ' ',
    /// each line trimmed. Null gives "".</summary>
    public static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Replace(' ', ' ').Replace("\r", "");
        var sb = new StringBuilder();
        var first = true;
        foreach (var line in t.Split('\n'))
        {
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(SpaceRunRx().Replace(line, " ").Trim());
        }

        return sb.ToString();
    }

    /// <summary>Length in minutes; an End of 00:00 (or any End &lt;= Start) means the next day.</summary>
    public static int Minutes(TimeOnly start, TimeOnly end)
    {
        var m = (int)(end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;
        return end <= start ? m + 24 * 60 : m;
    }
}

/// <summary>Wraps the real free/busy source for the AI meeting-times test. Lets through only the allowed address,
/// and only once the model call has returned (SPEC 21.1). A refused call never reaches Outlook.</summary>
internal sealed class GuardedFreeBusySource(IFreeBusySource inner, string allowedEmail, Func<bool> modelReturned)
    : IFreeBusySource
{
    private readonly List<WorkPriority> _priorities = [];

    /// <summary>Null while nothing was refused; else the FIRST reason: "address", "before-model" or "current-user".</summary>
    public string? Tripped { get; private set; }

    /// <summary>GetFreeBusyAsync calls that reached the inner source.</summary>
    public int Calls { get; private set; }

    /// <summary>The priority of each call that reached the inner source, in order.</summary>
    public IReadOnlyList<WorkPriority> Priorities => _priorities;

    /// <exception cref="InvalidOperationException">Always; sets Tripped.</exception>
    public Task<Recipient> GetCurrentUserAsync(WorkPriority priority, CancellationToken ct)
    {
        Tripped ??= "current-user";
        throw new InvalidOperationException("Live test refused the current-user lookup.");
    }

    public Task<string?> GetFreeBusyAsync(string email, DateTime from, DateTime to, int intervalMinutes,
        WorkPriority priority, CancellationToken ct)
    {
        if (!string.Equals(email?.Trim(), allowedEmail?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Tripped ??= "address";
            return Task.FromResult<string?>(null);
        }

        if (!modelReturned())
        {
            Tripped ??= "before-model";
            return Task.FromResult<string?>(null);
        }

        Calls++;
        _priorities.Add(priority);
        return inner.GetFreeBusyAsync(email!.Trim(), from, to, intervalMinutes, priority, ct);
    }
}
