using System.Globalization;

namespace ClippysRevenge.Core;

public enum SenderTld { European, OtherCountry, Generic }

/// <summary>Is the sender in Europe (SPEC 21.4)? Pure; never throws.</summary>
public static class SenderRegion
{
    private static readonly HashSet<string> Tlds = new(StringComparer.Ordinal)
    {
        "ad", "al", "at", "ax", "ba", "be", "bg", "ch", "cy", "cz", "de", "dk", "ee", "es", "eu", "fi", "fo", "fr",
        "gb", "gg", "gi", "gr", "hr", "hu", "ie", "im", "is", "it", "je", "li", "lt", "lu", "lv", "mc", "md", "me",
        "mk", "mt", "nl", "no", "pl", "pt", "ro", "rs", "se", "si", "sk", "sm", "ua", "uk", "va",
    };

    private static readonly Lazy<TimeZoneInfo?> Central = new(FindCentralEurope);

    public static IReadOnlySet<string> EuropeanTlds => Tlds;

    public static bool IsEurope(string? senderSmtp, string? transportHeaders)
    {
        TimeSpan? offset = null;
        if (MailText.Headers(transportHeaders).TryGetValue("Date", out var date)) offset = DateOffset(date);
        var tld = Tld(senderSmtp);
        if (tld == SenderTld.OtherCountry) return false;
        if (offset is null) return tld == SenderTld.European;
        var h = offset.Value;
        if (h == TimeSpan.FromHours(1) || h == TimeSpan.FromHours(2)) return true;
        if (h == TimeSpan.Zero || h == TimeSpan.FromHours(3)) return tld == SenderTld.European;
        return false;
    }

    /// <summary>The zone offset of an RFC 5322 Date value, or null (no / unknown / invalid offset).</summary>
    public static TimeSpan? DateOffset(string? dateHeaderValue)
    {
        if (string.IsNullOrWhiteSpace(dateHeaderValue)) return null;
        var v = dateHeaderValue.Trim();
        if (v.EndsWith(')'))
        {
            var open = v.LastIndexOf('(');
            if (open < 0) return null;
            v = v[..open].Trim();
        }

        var tokens = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var t = tokens[^1];

        if (t.Equals("GMT", StringComparison.OrdinalIgnoreCase) || t.Equals("UT", StringComparison.OrdinalIgnoreCase)
            || t.Equals("UTC", StringComparison.OrdinalIgnoreCase) || t.Equals("Z", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.Zero;

        if (t.Length != 5 || (t[0] != '+' && t[0] != '-')) return null;
        for (var i = 1; i < 5; i++)
            if (t[i] is < '0' or > '9') return null;
        var hh = int.Parse(t.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var mm = int.Parse(t.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (hh > 14 || mm > 59) return null;
        if (t == "-0000") return null;
        var span = new TimeSpan(hh, mm, 0);
        return t[0] == '-' ? span.Negate() : span;
    }

    /// <summary>Classifies the last label of the address's domain.</summary>
    public static SenderTld Tld(string? senderSmtp)
    {
        if (string.IsNullOrWhiteSpace(senderSmtp)) return SenderTld.Generic;
        var at = senderSmtp.LastIndexOf('@');
        if (at < 0) return SenderTld.Generic;
        var domain = senderSmtp[(at + 1)..].Trim();
        if (domain.EndsWith('.')) domain = domain[..^1];
        domain = domain.ToLowerInvariant();
        var label = domain[(domain.LastIndexOf('.') + 1)..];
        if (Tlds.Contains(label)) return SenderTld.European;
        if (label.Length == 2 && label.All(c => c is >= 'a' and <= 'z')) return SenderTld.OtherCountry;
        return SenderTld.Generic;
    }

    /// <summary>Europe/Berlin, else "W. Europe Standard Time", else null. Looked up once and cached.</summary>
    public static TimeZoneInfo? CentralEurope() => Central.Value;

    private static TimeZoneInfo? FindCentralEurope()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return null;
    }
}
