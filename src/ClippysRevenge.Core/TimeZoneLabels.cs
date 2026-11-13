using System.Globalization;

namespace ClippysRevenge.Core;

public static class TimeZoneLabels
{
    private static readonly Dictionary<string, string> UsZones = Build();

    private static readonly HashSet<string> UtcIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "UTC", "Etc/UTC", "Etc/UCT", "Etc/Zulu", "Etc/GMT", "Coordinated Universal Time",
    };

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string label, params string[] ids)
        {
            foreach (var id in ids) d[id] = label;
        }
        Add("PT", "Pacific Standard Time", "America/Los_Angeles", "US/Pacific");
        Add("MT", "Mountain Standard Time", "US Mountain Standard Time", "America/Denver", "America/Boise",
            "America/Phoenix", "US/Mountain", "US/Arizona");
        Add("CT", "Central Standard Time", "America/Chicago", "America/Indiana/Knox", "America/Indiana/Tell_City",
            "America/Menominee", "America/North_Dakota/Center", "America/North_Dakota/New_Salem",
            "America/North_Dakota/Beulah", "US/Central");
        Add("ET", "Eastern Standard Time", "US Eastern Standard Time", "America/New_York", "America/Detroit",
            "America/Kentucky/Louisville", "America/Kentucky/Monticello", "America/Louisville",
            "America/Indiana/Indianapolis", "America/Indianapolis", "America/Indiana/Vincennes",
            "America/Indiana/Winamac", "America/Indiana/Marengo", "America/Indiana/Petersburg",
            "America/Indiana/Vevay", "US/Eastern", "US/East-Indiana", "US/Michigan");
        Add("AKT", "Alaskan Standard Time", "America/Anchorage", "America/Juneau", "America/Sitka",
            "America/Nome", "America/Yakutat", "America/Metlakatla", "US/Alaska");
        Add("HT", "Hawaiian Standard Time", "Pacific/Honolulu", "US/Hawaii");
        return d;
    }

    public static string For(TimeZoneInfo tz, DateTime? at = null)
    {
        ArgumentNullException.ThrowIfNull(tz);
        if (UsZones.TryGetValue(tz.Id, out var label)) return label;
        if (UtcIds.Contains(tz.Id)) return "UTC";

        var std = tz.StandardName.Trim();
        if (std.Length is >= 2 and <= 5 && std.All(c => c is >= 'A' and <= 'Z')) return std;

        var offset = at is null ? tz.BaseUtcOffset : tz.GetUtcOffset(at.Value);
        var abs = offset < TimeSpan.Zero ? offset.Negate() : offset;
        var sign = offset >= TimeSpan.Zero ? "+" : "-";
        return "UTC" + sign + abs.Hours.ToString("00", CultureInfo.InvariantCulture) + ":"
            + abs.Minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string Resolve(string? overrideLabel, TimeZoneInfo tz, DateTime? at = null)
    {
        ArgumentNullException.ThrowIfNull(tz);
        return string.IsNullOrWhiteSpace(overrideLabel) ? For(tz, at) : overrideLabel.Trim();
    }
}
