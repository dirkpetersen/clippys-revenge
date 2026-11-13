using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClippysRevenge.Core;

/// <summary>The only update URLs and hosts in the app (SPEC 20.3, 20.11).</summary>
public static class UpdateEndpoints
{
    public const string LatestReleaseApi = "https://api.github.com/repos/dirkpetersen/clippys-revenge/releases/latest";
    public const string ReleasesPage = "https://github.com/dirkpetersen/clippys-revenge/releases";
    public const string DocsUrl = "https://dirkpetersen.github.io/clippys-revenge/";
    public const string ExeAssetName = "ClippysRevenge.exe";
    public const string SumsAssetName = "SHA256SUMS.txt";
    public const string ApiAccept = "application/vnd.github+json";
    public const string ApiVersionHeader = "X-GitHub-Api-Version";
    public const string ApiVersion = "2022-11-28";
    public const string DownloadAccept = "application/octet-stream";

    private const string DownloadBase = "https://github.com/dirkpetersen/clippys-revenge/releases/download/";

    /// <summary>github.com, release-assets.githubusercontent.com, objects.githubusercontent.com (in that order).</summary>
    public static IReadOnlyList<string> AllowedDownloadHosts { get; } =
        ["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    /// <summary>The release asset URL for a tag and one of the two asset names.</summary>
    /// <exception cref="ArgumentException">tag fails SemVersion.TryParseReleaseTag, or assetName is neither asset name.</exception>
    public static Uri AssetUrl(string tag, string assetName)
    {
        if (!SemVersion.TryParseReleaseTag(tag, out _))
        {
            throw new ArgumentException("Not a release tag.", nameof(tag));
        }
        if (!string.Equals(assetName, ExeAssetName, StringComparison.Ordinal)
            && !string.Equals(assetName, SumsAssetName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a release asset name.", nameof(assetName));
        }
        return new Uri(DownloadBase + tag + "/" + assetName);
    }

    /// <summary>ReleasesPage + "/tag/" + tag.</summary>
    /// <exception cref="ArgumentException">tag fails SemVersion.TryParseReleaseTag.</exception>
    public static string ReleaseNotesUrl(string tag)
    {
        if (!SemVersion.TryParseReleaseTag(tag, out _))
        {
            throw new ArgumentException("Not a release tag.", nameof(tag));
        }
        return ReleasesPage + "/tag/" + tag;
    }

    /// <summary>True only for an absolute https URI on the default port, without user info, whose IdnHost equals
    /// (OrdinalIgnoreCase) one of AllowedDownloadHosts. Null gives false.</summary>
    public static bool IsAllowedRedirect(Uri? target)
    {
        if (target is null || !target.IsAbsoluteUri)
        {
            return false;
        }
        if (!string.Equals(target.Scheme, "https", StringComparison.Ordinal)
            || !target.IsDefaultPort
            || target.UserInfo.Length != 0)
        {
            return false;
        }
        foreach (var host in AllowedDownloadHosts)
        {
            if (string.Equals(target.IdnHost, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>"ClippysRevenge/" + running (e.g. "ClippysRevenge/0.0.6").</summary>
    public static string UserAgent(SemVersion running)
    {
        ArgumentNullException.ThrowIfNull(running);
        return "ClippysRevenge/" + running;
    }
}

/// <param name="Tag">The release tag, "v&lt;MAJOR&gt;.&lt;MINOR&gt;.&lt;PATCH&gt;".</param>
/// <param name="Version">The version of the tag.</param>
/// <param name="PublishedUtc">The release's published_at, or null.</param>
/// <param name="ExeSize">Size of the exe asset in bytes.</param>
/// <param name="ExeDigest">Lower-case hex from the asset's "digest" ("sha256:&lt;hex&gt;"), or null.</param>
/// <param name="SumsSize">Size of the SHA256SUMS.txt asset in bytes.</param>
public sealed record ReleaseInfo(string Tag, SemVersion Version, DateTimeOffset? PublishedUtc, long ExeSize,
    string? ExeDigest, long SumsSize)
{
    public string NotesUrl => UpdateEndpoints.ReleaseNotesUrl(Tag);
}

/// <summary>Exactly one of Release and Error is non-null. Error is an UpdateTexts reason.</summary>
public sealed record ReleaseParseResult(ReleaseInfo? Release, string? Error);

public static class ReleaseParser
{
    public const int MaxJsonBytes = 1_048_576;
    public const long MinExeBytes = 1_048_576;
    public const long MaxExeBytes = 419_430_400;
    public const int MaxSumsBytes = 65_536;

    private static readonly Regex DigestGrammar = new(
        "^sha256:([0-9a-fA-F]{64})\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>SPEC 20.4. Never throws.</summary>
    public static ReleaseParseResult Parse(string? json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or RegexMatchTimeoutException
            or FormatException or ArgumentException)
        {
            return Fail(UpdateTexts.ReasonBadJson);
        }
    }

    private static ReleaseParseResult Fail(string reason) => new(null, reason);

    private static ReleaseParseResult ParseCore(string? json)
    {
        if (json is null || json.Length > MaxJsonBytes)
        {
            return Fail(UpdateTexts.ReasonBadJson);
        }
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail(UpdateTexts.ReasonBadJson);
        }

        foreach (var flag in new[] { "draft", "prerelease" })
        {
            if (!root.TryGetProperty(flag, out var f))
            {
                continue;
            }
            if (f.ValueKind == JsonValueKind.True)
            {
                return Fail(UpdateTexts.ReasonNotFullRelease);
            }
            if (f.ValueKind != JsonValueKind.False)
            {
                return Fail(UpdateTexts.ReasonBadJson);
            }
        }

        if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String
            || tagEl.GetString() is not { } tag || !SemVersion.TryParseReleaseTag(tag, out var version))
        {
            return Fail(UpdateTexts.ReasonNotFullRelease);
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return Fail(UpdateTexts.ReasonNoAssets);
        }
        JsonElement? exe = null;
        JsonElement? sums = null;
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object
                || !a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (exe is null && n.ValueEquals(UpdateEndpoints.ExeAssetName))
            {
                exe = a;
            }
            else if (sums is null && n.ValueEquals(UpdateEndpoints.SumsAssetName))
            {
                sums = a;
            }
        }
        if (exe is null || sums is null
            || !TryAssetSize(exe.Value, MinExeBytes, MaxExeBytes, out var exeSize)
            || !TryAssetSize(sums.Value, 1, MaxSumsBytes, out var sumsSize))
        {
            return Fail(UpdateTexts.ReasonNoAssets);
        }

        DateTimeOffset? published = null;
        if (root.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pub.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            published = parsed;
        }

        string? digest = null;
        if (exe.Value.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String
            && d.GetString() is { } ds)
        {
            var m = DigestGrammar.Match(ds);
            if (m.Success)
            {
                digest = m.Groups[1].Value.ToLowerInvariant();
            }
        }

        return new ReleaseParseResult(new ReleaseInfo(tag, version, published, exeSize, digest, sumsSize), null);
    }

    private static bool TryAssetSize(JsonElement asset, long min, long max, out long size)
    {
        size = 0;
        if (asset.TryGetProperty("state", out var st)
            && (st.ValueKind != JsonValueKind.String || !st.ValueEquals("uploaded")))
        {
            return false;
        }
        if (!asset.TryGetProperty("size", out var sz) || sz.ValueKind != JsonValueKind.Number
            || !sz.TryGetInt64(out size))
        {
            return false;
        }
        return size >= min && size <= max;
    }
}

public static class Sha256Sums
{
    private static readonly Regex Line = new(
        @"^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Lower-case hex of the entry for fileName (ordinal), or null when there is none or two entries
    /// disagree. Never throws.</summary>
    public static string? Find(string? text, string fileName)
    {
        if (text is null || fileName is null)
        {
            return null;
        }
        try
        {
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text[1..];
            }
            string? found = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.EndsWith('\r') ? raw[..^1] : raw;
                var m = Line.Match(line);
                if (!m.Success || !string.Equals(m.Groups[2].Value, fileName, StringComparison.Ordinal))
                {
                    continue;
                }
                var hash = m.Groups[1].Value.ToLowerInvariant();
                if (found is not null && found != hash)
                {
                    return null;
                }
                found = hash;
            }
            return found;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}

public static class UpdateHash
{
    /// <summary>SHA-256 of the file, lower-case hex. Streams the file.</summary>
    /// <exception cref="IOException">and the other File.OpenRead exceptions, unchanged.</exception>
    public static async Task<string> OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return ToHex(hash);
    }

    /// <summary>Lower-case hex.</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Exactly 64 hex chars (any case).</summary>
    public static bool IsSha256Hex(string? text)
    {
        if (text is null || text.Length != 64)
        {
            return false;
        }
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>OrdinalIgnoreCase equality of two hex strings; false when either is null.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
