using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace ClippysRevenge.Core;

/// <summary>%LOCALAPPDATA%\ClippysRevenge\links (SPEC 24.2, 5.1).</summary>
public sealed record LinkPaths(string Folder)
{
    public const string FolderName = "links";
    private const string TempExtension = ".bin";
    private const int TempNameLength = 32 + 4;

    /// <exception cref="ArgumentException">localAppData blank or not fully qualified.</exception>
    public static LinkPaths Default(string localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new ArgumentException("localAppData must be a fully qualified path.", nameof(localAppData));
        return new LinkPaths(Path.Combine(localAppData, "ClippysRevenge", FolderName));
    }

    /// <summary>32 lower-case hex chars from 16 random bytes + ".bin". Never derived from the attachment name.</summary>
    public static string NewFileName() =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) + TempExtension;

    /// <summary>^[0-9a-f]{32}\.bin$, ordinal (lower case only). Null = false.</summary>
    public static bool IsTempFileName(string? name)
    {
        if (name is null || name.Length != TempNameLength) return false;
        for (var i = 0; i < 32; i++)
        {
            var c = name[i];
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return string.CompareOrdinal(name, 32, TempExtension, 0, 4) == 0;
    }
}

public static class LinkFileName
{
    /// <summary>FileNames.SafeFileName(original). Never empty ("attachment").</summary>
    public static string Safe(string? original) => FileNames.SafeFileName(original);

    /// <summary>Safe(original), then every ' becomes _, then a leading - or . becomes _ (SPEC 24.5).</summary>
    public static string ForShell(string? original)
    {
        var s = Safe(original).Replace('\'', '_');
        if (s[0] is '-' or '.') s = "_" + s[1..];
        return s;
    }
}

public static class FileDownloadHeaders
{
    /// <summary>The five 200-response headers in this order (SPEC 24.4).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(string? originalName, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var safe = LinkFileName.Safe(originalName);
        return
        [
            new("Content-Type", "application/octet-stream"),
            new("Content-Disposition", $"attachment; filename=\"{AsciiName(safe)}\"; filename*=UTF-8''{PercentEncode(safe)}"),
            new("Content-Length", length.ToString(CultureInfo.InvariantCulture)),
            new("X-Content-Type-Options", "nosniff"),
            new("Cache-Control", "no-store"),
        ];
    }

    /// <summary>The ASCII filename="" form of an already-Safe name.</summary>
    public static string AsciiName(string safeName)
    {
        ArgumentNullException.ThrowIfNull(safeName);
        var sb = new StringBuilder();
        var rest = safeName.AsSpan();
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var consumed);
            rest = rest[consumed..];
            if (status != System.Buffers.OperationStatus.Done)
            {
                sb.Append('_');
                continue;
            }
            var v = rune.Value;
            if (v is '"' or '\\') continue;
            sb.Append(v is >= 0x20 and <= 0x7E ? (char)v : '_');
        }
        return sb.Length == 0 ? FileNames.Fallback : sb.ToString();
    }

    /// <summary>RFC 5987 value without the UTF-8'' prefix.</summary>
    public static string PercentEncode(string safeName)
    {
        ArgumentNullException.ThrowIfNull(safeName);
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(safeName))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

public static class LinkTexts
{
    public const string Note = "One download only. If curl answers 401, CLIPPY_TOKEN isn't set in this shell: ask the user to copy the auth token from the Clippy tray menu and add export CLIPPY_TOKEN=<token> to ~/.bashrc.";
    public const string ToolDescription = "Makes a one-time download link for one attachment of an email, for use from WSL or another shell. Use the id from search_emails; pass filename or position when the email has more than one attachment. Run the returned curl command in the shell: it saves the file in the current folder. The link works once and expires in 10 minutes. The file content is not returned to you. The shell needs CLIPPY_TOKEN set (see note in the result). To save the file on the Windows side instead, use get_attachment.";
    public const string MaskedWslTokenLine = "export CLIPPY_TOKEN=••••••••";
    public const string FolderUnusable = "Clippy can't use its private download folder. Use get_attachment instead.";
    public const string TooManyOpen = "Too many open download links (10). Download them or wait until they expire (10 minutes), then try again.";
    public const string TooManyBytes = "The open download links already hold 200 MB. Download them or wait until they expire, then try again.";

    /// <summary>"The attachment is n MB; attachment_link handles up to 100 MB. Use get_attachment to save it on the Windows side."</summary>
    public static string TooBig(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var mb = (bytes + 1048575) / 1048576;
        return $"The attachment is {mb.ToString(CultureInfo.InvariantCulture)} MB; attachment_link handles up to 100 MB. Use get_attachment to save it on the Windows side.";
    }

    public static string RateLimited(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        return $"Too many download links in the last 10 minutes (20). Try again in {minutes.ToString(CultureInfo.InvariantCulture)} min.";
    }

    /// <summary>"export CLIPPY_TOKEN=" + token.</summary>
    /// <exception cref="ArgumentException">token null/empty, or has a char outside [A-Za-z0-9._~+/=-].</exception>
    public static string WslTokenLine(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        foreach (var c in token)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '='))
                throw new ArgumentException("Token must contain only letters, digits and -._~+/=.", nameof(token));
        }
        return "export CLIPPY_TOKEN=" + token;
    }

    /// <summary>http://127.0.0.1:port/files/token.</summary>
    public static string Url(int port, string token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (!AttachmentLinks.IsWellFormedToken(token))
            throw new ArgumentException("token is not well-formed.", nameof(token));
        return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{AttachmentLinks.FilesPath}/{token}";
    }

    /// <summary>curl -fsS -H "Authorization: Bearer $CLIPPY_TOKEN" -o 'shellName' 'url'. The real token is never passed in.</summary>
    public static string Curl(string shellName, string url)
    {
        CheckQuotable(shellName, nameof(shellName));
        CheckQuotable(url, nameof(url));
        return "curl -fsS -H \"Authorization: Bearer $CLIPPY_TOKEN\" -o '" + shellName + "' '" + url + "'";
    }

    public static string FormatExpires(DateTimeOffset at, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);
        return TimeZoneInfo.ConvertTime(at, tz).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static void CheckQuotable(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Value must not be empty.", name);
        foreach (var c in value)
            if (c == '\'' || char.IsControl(c))
                throw new ArgumentException("Value must not contain ' or control characters.", name);
    }
}

/// <summary>The attachment_link result (SPEC 24.1). ToString() never shows the url.</summary>
public sealed record AttachmentLinkResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("expires_at")] string ExpiresAt,
    [property: JsonPropertyName("curl")] string Curl,
    [property: JsonPropertyName("note")] string Note)
{
    public static AttachmentLinkResult Create(AttachmentLink link, int port, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(tz);
        var url = LinkTexts.Url(port, link.Token);
        return new AttachmentLinkResult(
            url,
            link.FileName,
            link.Size,
            LinkTexts.FormatExpires(link.ExpiresAt, tz),
            LinkTexts.Curl(LinkFileName.ForShell(link.FileName), url),
            LinkTexts.Note);
    }

    public override string ToString() => nameof(AttachmentLinkResult);
}
