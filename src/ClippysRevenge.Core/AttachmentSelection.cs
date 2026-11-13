using System.Globalization;
using ModelContextProtocol;

namespace ClippysRevenge.Core;

/// <summary>Pure selection rules for get_attachment and save_attachments (SPEC §11.3, §11.4).</summary>
public static class AttachmentSelection
{
    public const int MaxListed = 20;

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    public static AttachmentInfo SelectOne(IReadOnlyList<AttachmentInfo> attachments, string? filename, int? position)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var name = string.IsNullOrWhiteSpace(filename) ? null : filename.Trim();
        if (attachments.Count == 0) throw new McpException("This email has no attachments.");

        if (position is { } p)
        {
            var atPos = attachments.FirstOrDefault(a => a.Position == p);
            if (atPos is null)
                throw new McpException(
                    $"There is no attachment at position {N(p)}. This email has {N(attachments.Count)} attachment(s): {Describe(attachments)}.");
            if (name is not null && !string.Equals(atPos.FileName, name, StringComparison.OrdinalIgnoreCase))
                throw new McpException(
                    $"The attachment at position {N(p)} is \"{atPos.FileName}\", not \"{name}\". Pass only one of filename and position, or matching values.");
            return atPos;
        }

        if (name is not null)
        {
            var matches = attachments.Where(a => string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Position).ToList();
            if (matches.Count == 0)
                throw new McpException($"This email has no attachment named \"{name}\". Its attachments: {Describe(attachments)}.");
            if (matches.Count > 1)
                throw new McpException(
                    $"{N(matches.Count)} attachments are named \"{name}\" (positions {string.Join(", ", matches.Select(m => N(m.Position)))}). Pass position to choose one.");
            return matches[0];
        }

        var real = attachments.Where(a => !a.IsInline).ToList();
        if (real.Count == 1) return real[0];
        if (attachments.Count == 1) return attachments[0];
        throw new McpException(
            $"This email has {N(attachments.Count)} attachments. Pass filename or position to choose one: {Describe(attachments)}.");
    }

    public static IReadOnlyList<AttachmentInfo> SelectForBulk(
        IReadOnlyList<AttachmentInfo> attachments, string? attachmentName, string? attachmentExt, bool includeInline)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var name = attachmentName?.Trim();
        var ext = attachmentExt?.Trim();
        if (!string.IsNullOrEmpty(ext) && ext[0] == '.') ext = ext[1..];
        var hasName = !string.IsNullOrEmpty(name);
        var hasExt = !string.IsNullOrEmpty(ext);
        var suffix = hasExt ? "." + ext : "";
        return attachments
            .Where(a => includeInline || !a.IsInline)
            .Where(a => !hasName || AsciiContains(a.FileName, name!))
            .Where(a => !hasExt || AsciiEndsWith(a.FileName, suffix))
            .OrderBy(a => a.Position)
            .ToList();
    }

    public static AttachmentInfo? MatchLive(IReadOnlyList<AttachmentInfo> live, AttachmentInfo wanted)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(wanted);
        var same = live.FirstOrDefault(a => a.Position == wanted.Position
            && string.Equals(a.FileName, wanted.FileName, StringComparison.OrdinalIgnoreCase));
        if (same is not null) return same;
        var byName = live.Where(a => string.Equals(a.FileName, wanted.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public static string Describe(IReadOnlyList<AttachmentInfo> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0) return "(none)";
        var ordered = attachments.OrderBy(a => a.Position).ToList();
        var text = string.Join(", ", ordered.Take(MaxListed)
            .Select(a => $"{N(a.Position)} \"{a.FileName}\"{(a.IsInline ? " (inline)" : "")}"));
        if (ordered.Count > MaxListed) text += $", … ({N(ordered.Count - MaxListed)} more)";
        return text;
    }

    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    private static bool AsciiEquals(string s, int at, string needle)
    {
        for (var i = 0; i < needle.Length; i++)
            if (Fold(s[at + i]) != Fold(needle[i])) return false;
        return true;
    }

    private static bool AsciiContains(string s, string needle)
    {
        for (var i = 0; i + needle.Length <= s.Length; i++)
            if (AsciiEquals(s, i, needle)) return true;
        return false;
    }

    private static bool AsciiEndsWith(string s, string suffix) =>
        s.Length >= suffix.Length && AsciiEquals(s, s.Length - suffix.Length, suffix);
}
