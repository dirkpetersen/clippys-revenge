using System.Globalization;

namespace ClippysRevenge.Core;

public static class SlotFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string WindowMessage = "Window hours must satisfy 0 <= start < end <= 24.";

    public static string Format(int n, DateTime start, DateTime end, string? tzLabel,
                                int windowStartHour, int windowEndHour)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), "Slot number must be 1 or greater.");
        if (windowStartHour < 0 || windowStartHour >= 24)
            throw new ArgumentOutOfRangeException(nameof(windowStartHour), WindowMessage);
        if (windowEndHour > 24 || windowEndHour < 1)
            throw new ArgumentOutOfRangeException(nameof(windowEndHour), WindowMessage);
        if (windowStartHour >= windowEndHour)
            throw new ArgumentOutOfRangeException(nameof(windowStartHour), WindowMessage);
        if (end <= start)
            throw new ArgumentException("Slot end must be after its start.", nameof(end));
        var midnightNext = end.TimeOfDay == TimeSpan.Zero && end.Date == start.Date.AddDays(1);
        if (end.Date != start.Date && !midnightNext)
            throw new ArgumentException("Slot must start and end on the same day.", nameof(end));

        var ampm = NeedsAmPm(windowStartHour, windowEndHour);
        var sh = start.Hour;
        var eh = midnightNext ? 0 : end.Hour;
        var sText = Clock(sh, start.Minute);
        var eText = Clock(eh, midnightNext ? 0 : end.Minute);
        if (ampm)
        {
            var sSuffix = sh < 12 ? "am" : "pm";
            var eSuffix = eh < 12 ? "am" : "pm";
            if (sSuffix != eSuffix) sText += sSuffix;
            eText += eSuffix;
        }

        var result = $"{n.ToString(Inv)}. {start.ToString("ddd", Inv)} {start.ToString("M/d", Inv)} {sText}-{eText}";
        return string.IsNullOrWhiteSpace(tzLabel) ? result : result + " " + tzLabel.Trim();
    }

    public static bool NeedsAmPm(int windowStartHour, int windowEndHour) =>
        windowStartHour < 7 || windowEndHour > 19;

    private static string Clock(int hour, int minute)
    {
        var h12 = hour % 12 == 0 ? 12 : hour % 12;
        return minute == 0
            ? h12.ToString(Inv)
            : h12.ToString(Inv) + ":" + minute.ToString("00", Inv);
    }
}
