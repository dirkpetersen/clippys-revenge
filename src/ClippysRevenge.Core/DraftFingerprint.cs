using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClippysRevenge.Core;

/// <summary>The saved draft's properties as read through COM (SPEC 23.4). Null means "".</summary>
/// <param name="Importance">(int)MailItem.Importance.</param>
/// <param name="AttachmentNames">File names in position order; null = none; a null entry is "".</param>
public sealed record DraftFingerprintInput(string? Subject, string? To, string? Cc, string? Bcc, string? HtmlBody,
    string? Categories, int Importance, IReadOnlyList<string?>? AttachmentNames);

/// <summary>SPEC 23.4 unedited test. Pure, thread-safe.</summary>
public static class DraftFingerprint
{
    public const int Length = 64;

    /// <summary>64 lower-case hex chars: SHA-256 over UTF-8 of Canonical(input).</summary>
    /// <exception cref="ArgumentNullException">input is null.</exception>
    public static string Compute(DraftFingerprintInput input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(input))));

    /// <summary>The hashed text: name=len:value lines, in a fixed order. Exposed for tests.</summary>
    /// <exception cref="ArgumentNullException">input is null.</exception>
    internal static string Canonical(DraftFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new StringBuilder();
        Add(sb, "subject", input.Subject);
        Add(sb, "to", input.To);
        Add(sb, "cc", input.Cc);
        Add(sb, "bcc", input.Bcc);
        Add(sb, "html", input.HtmlBody);
        Add(sb, "categories", input.Categories);
        Add(sb, "importance", input.Importance.ToString(CultureInfo.InvariantCulture));
        Add(sb, "attachments", input.AttachmentNames is null ? "" : string.Join("\n", input.AttachmentNames.Select(n => n ?? "")));
        return sb.ToString();
    }

    /// <summary>Exactly 64 chars, each 0-9 or a-f. False for null and upper-case hex.</summary>
    public static bool IsValid([NotNullWhen(true)] string? fingerprint)
    {
        if (fingerprint is null || fingerprint.Length != Length) return false;
        foreach (var c in fingerprint)
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;
        return true;
    }

    private static void Add(StringBuilder sb, string name, string? value)
    {
        var v = value ?? "";
        sb.Append(name).Append('=').Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('\n');
    }
}
