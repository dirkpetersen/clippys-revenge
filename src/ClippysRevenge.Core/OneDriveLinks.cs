using System.Diagnostics.CodeAnalysis;

namespace ClippysRevenge.Core;

/// <summary>A kept link. Url: the (Safe Links-unwrapped) URL, Uri.AbsoluteUri. NormalizedUrl: the dedupe key.
/// Name: the display name of SPEC 26.3.</summary>
public sealed record FoundLink(string Url, string NormalizedUrl, string Name);

/// <summary>
/// Finds OneDrive/SharePoint links in text (SPEC 26.3). Pure string work: nothing here makes a request,
/// and nothing logs a URL or name.
/// </summary>
public static class OneDriveLinks
{
    public const int MaxPerBody = 10;
    public const int MaxNameChars = 120;

    private const string SafeLinksSuffix = ".safelinks.protection.outlook.com";
    private const string SharePointSuffix = ".sharepoint.com";
    private const string TrailingPunctuation = ".,;:!?)]";

    /// <summary>SPEC 26.3 "Finding links". Body order, deduped by NormalizedUrl, at most MaxPerBody (counted
    /// after the host filter). Null/empty gives empty. Never throws.</summary>
    public static IReadOnlyList<FoundLink> Find(string? body)
    {
        var result = new List<FoundLink>();
        if (string.IsNullOrEmpty(body)) return result;

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pos = 0;
            while (pos < body.Length && result.Count < MaxPerBody)
            {
                var start = body.IndexOf("https://", pos, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;

                var end = start;
                while (end < body.Length && !IsCandidateEnd(body[end])) end++;
                pos = end;

                var candidate = body[start..end].TrimEnd(TrailingPunctuation.ToCharArray());
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
                if (!TryUnwrap(uri, out var inner)) continue;

                var normalized = Normalize(inner);
                if (!seen.Add(normalized)) continue;

                var name = DirectName(inner)
                    ?? TextBeforeName(body, start)
                    ?? HostName(inner);
                result.Add(new FoundLink(inner.AbsoluteUri, normalized, name));
            }
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException or InvalidOperationException)
        {
            // Never throws: keep what was found.
        }
        return result;
    }

    /// <summary>One URL (a cloud attachment's): trim, Safe Links unwrap, host rule. False when it fails.
    /// Name uses the direct-path or host fallback rule (no surrounding text). Never throws.</summary>
    public static bool TryParse(string? url, [NotNullWhen(true)] out FoundLink? link)
    {
        link = null;
        try
        {
            var trimmed = url?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
            if (!TryUnwrap(uri, out var inner)) return false;
            link = new FoundLink(inner.AbsoluteUri, Normalize(inner), DirectName(inner) ?? HostName(inner));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException or InvalidOperationException)
        {
            link = null;
            return false;
        }
    }

    /// <summary>SPEC 26.3 "Host rule". Null throws ArgumentNullException; a relative URI is false.</summary>
    public static bool IsMicrosoftFileHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.UserInfo.Length != 0) return false;
        if (!uri.IsDefaultPort) return false;
        if (uri.HostNameType != UriHostNameType.Dns) return false;

        var h = NormalizedHost(uri);
        if (h is "onedrive.live.com" or "d.docs.live.net" or "1drv.ms") return true;
        return h.Length > SharePointSuffix.Length && h.EndsWith(SharePointSuffix, StringComparison.Ordinal);
    }

    /// <summary>IdnHost, lower-case invariant, one trailing '.' removed.</summary>
    internal static string NormalizedHost(Uri uri)
    {
        var h = uri.IdnHost.ToLowerInvariant();
        return h.EndsWith('.') ? h[..^1] : h;
    }

    /// <summary>SPEC 26.3 Safe Links unwrap, then the host rule. False means the link is dropped.</summary>
    internal static bool UnwrapSafeLinks(Uri uri, [NotNullWhen(true)] out Uri? inner)
    {
        inner = null;
        var target = uri;
        if (uri.IsAbsoluteUri && uri.HostNameType == UriHostNameType.Dns
            && NormalizedHost(uri).EndsWith(SafeLinksSuffix, StringComparison.Ordinal))
        {
            var value = QueryValue(uri.Query, "url");
            if (value is null) return false;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var wrapped) || wrapped.Scheme != Uri.UriSchemeHttps)
                return false;
            target = wrapped;
        }
        if (!IsMicrosoftFileHost(target)) return false;
        inner = target;
        return true;
    }

    private static bool TryUnwrap(Uri uri, [NotNullWhen(true)] out Uri? inner) => UnwrapSafeLinks(uri, out inner);

    private static string? QueryValue(string query, string name)
    {
        if (query.StartsWith('?')) query = query[1..];
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = eq < 0 ? "" : pair[(eq + 1)..];
            return Uri.UnescapeDataString(raw);
        }
        return null;
    }

    private static bool IsCandidateEnd(char c) => char.IsWhiteSpace(c) || c is '<' or '>' or '"';

    private static string Normalize(Uri u) =>
        "https://" + NormalizedHost(u) + u.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);

    private static string? DirectName(Uri u)
    {
        var host = NormalizedHost(u);
        if (!host.EndsWith(SharePointSuffix, StringComparison.Ordinal) && host != "d.docs.live.net") return null;
        var segs = OneDriveMap.DirectSegments(u);
        return segs is { Length: > 0 } ? segs[^1] : null;
    }

    private static string? TextBeforeName(string body, int candidateStart)
    {
        var lt = candidateStart - 1;
        if (lt < 0 || body[lt] != '<') return null;
        var from = lt == 0 ? -1 : body.LastIndexOfAny(['\n', '>'], lt - 1);
        var text = body[(from + 1)..lt].Trim();
        if (text.Length > MaxNameChars) text = text[^MaxNameChars..].TrimStart();
        return text.Length == 0 ? null : text;
    }

    private static string HostName(Uri u) => "a shared file on " + NormalizedHost(u);
}
