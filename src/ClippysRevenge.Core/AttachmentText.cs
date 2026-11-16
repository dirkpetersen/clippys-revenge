using System.Globalization;
using System.Text;

namespace ClippysRevenge.Core;

public enum ExtractStatus { Read, Truncated, Skipped }

/// <summary>SPEC 26.4. Text is "" when Skipped. Reason is set only when Skipped.
/// Truncated == (Status == ExtractStatus.Truncated).</summary>
public sealed record ExtractedText(ExtractStatus Status, string Text, string? Reason, bool Truncated)
{
    public static ExtractedText Skip(string reason) => new(ExtractStatus.Skipped, "", reason, false);
}

/// <summary>The format chosen from the file name (SPEC 26.4 table).</summary>
public enum AttachmentFormat
{
    Pdf, Word, Excel, PowerPoint, Text, Html, Eml,
    Image,              // InlineAttachments.IsImage(null, name): "image, not read", no draft note
    UnsupportedNoted,   // doc, dot, xls, xlt, xlsb, ppt, pps, rtf, odt, ods, odp, pages, numbers, key, zip, 7z, rar
    Unsupported,        // anything else (no draft note)
}

/// <summary>The skip reason texts of SPEC 26 (exact strings; they go into the prompt's reason="...").</summary>
public static class AttachmentReasons
{
    public const string Damaged = "the file seems damaged";
    public const string PasswordProtected = "it's password-protected";
    public const string NoText = "no text in it (maybe a scan)";
    public const string TooLarge = "larger than 25 MB";
    public const string Image = "image, not read";
    public const string UnknownKind = "Clippy can't read this kind of file";
    public const string NotSynced = "not synced to this PC";
    public const string FromOutside = "from outside your organization";
    public const string NotMicrosoftLink = "not a OneDrive or SharePoint link";
    public const string EarlierEmailGone = "the earlier email is no longer in your mailbox";
    public const string NotOnEmail = "not on the email any more";
    public const string TooManyFiles = "too many files";
    public const string TooMuchText = "too much attachment text for one draft";
    public const string OutOfTime = "ran out of time for attachments";
    public const string TookTooLong = "it took too long";
    public const string OutlookNotRunning = "Outlook isn't running";

    /// <summary>"Clippy can't read .&lt;ext&gt; files"; UnknownKind when ext is not 1-10 chars of [a-z0-9].</summary>
    public static string CantRead(string ext)
    {
        if (ext is null || ext.Length is < 1 or > 10) return UnknownKind;
        foreach (char c in ext)
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9')) return UnknownKind;
        return $"Clippy can't read .{ext} files";
    }
}

/// <summary>
/// Turns the bytes of one attachment into plain text for the drafting prompt (SPEC 26.4). Nothing is executed,
/// rendered or followed. Never logs, stores or caches text.
/// </summary>
public static class AttachmentText
{
    public const int MaxFileChars = 20_000;
    public const long MaxFileBytes = 25L * 1024 * 1024;     // 26,214,400
    public const int MaxTextBytes = 1024 * 1024;            // text/HTML: first 1 MiB
    public const int MaxPdfPages = 30;
    public const int MaxSheets = 5;
    public const int MaxSheetRows = 200;
    public const int MaxSheetColumns = 30;
    public const int MaxCellChars = 200;
    public const int MaxSlides = 60;

    static AttachmentText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly HashSet<string> NotedExtensions = new(StringComparer.Ordinal)
    {
        "doc", "dot", "xls", "xlt", "xlsb", "ppt", "pps", "rtf", "odt", "ods", "odp", "pages", "numbers", "key",
        "zip", "7z", "rar",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        "txt", "text", "csv", "tsv", "md", "markdown", "log", "json", "xml", "ics", "vcf",
    };

    /// <summary>Lower-case extension without the dot, trimmed; "" when none. Never throws (null gives "").</summary>
    public static string Extension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "";
        string name = fileName.Trim();
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot < slash) return "";
        return name[(dot + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>SPEC 26.4 table, by extension only. Never throws.</summary>
    public static AttachmentFormat FormatOf(string? fileName)
    {
        if (InlineAttachments.IsImage(null, fileName)) return AttachmentFormat.Image;
        string ext = Extension(fileName);
        switch (ext)
        {
            case "pdf": return AttachmentFormat.Pdf;
            case "docx": case "docm": case "dotx": return AttachmentFormat.Word;
            case "xlsx": case "xlsm": case "xltx": return AttachmentFormat.Excel;
            case "pptx": case "pptm": case "ppsx": return AttachmentFormat.PowerPoint;
            case "html": case "htm": return AttachmentFormat.Html;
            case "eml": return AttachmentFormat.Eml;
        }
        if (TextExtensions.Contains(ext)) return AttachmentFormat.Text;
        if (NotedExtensions.Contains(ext)) return AttachmentFormat.UnsupportedNoted;
        return AttachmentFormat.Unsupported;
    }

    /// <summary>SPEC 26.4. Never throws except OperationCanceledException when ct is cancelled.</summary>
    public static ExtractedText Extract(string fileName, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AttachmentFormat format = FormatOf(fileName);
        switch (format)
        {
            case AttachmentFormat.Image:
                return ExtractedText.Skip(AttachmentReasons.Image);
            case AttachmentFormat.UnsupportedNoted:
            case AttachmentFormat.Unsupported:
                return ExtractedText.Skip(AttachmentReasons.CantRead(Extension(fileName)));
        }
        if (bytes.Length > MaxFileBytes) return ExtractedText.Skip(AttachmentReasons.TooLarge);

        try
        {
            ReadOnlySpan<byte> span = bytes.Span;
            switch (format)
            {
                case AttachmentFormat.Pdf:
                    if (!HasPdfMagic(span)) return ExtractedText.Skip(AttachmentReasons.Damaged);
                    return PdfText.Extract(bytes, ct);

                case AttachmentFormat.Word:
                case AttachmentFormat.Excel:
                case AttachmentFormat.PowerPoint:
                    if (span.StartsWith(OleMagic)) return ExtractedText.Skip(AttachmentReasons.PasswordProtected);
                    if (!span.StartsWith(ZipMagic)) return ExtractedText.Skip(AttachmentReasons.Damaged);
                    return OoxmlText.Extract(format, bytes, ct);

                case AttachmentFormat.Eml:
                    return EmlText.Extract(bytes, ct);

                default: // Text, Html
                {
                    string? decoded = PlainText.Decode(span, out bool cut);
                    if (decoded is null) return ExtractedText.Skip(AttachmentReasons.Damaged);
                    if (format == AttachmentFormat.Html) decoded = DraftHtml.ToPlainText(decoded);
                    return Build(decoded, cut);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ExtractedText.Skip(AttachmentReasons.Damaged);
        }
    }

    private static ReadOnlySpan<byte> OleMagic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static ReadOnlySpan<byte> ZipMagic => [0x50, 0x4B, 0x03, 0x04];

    private static bool HasPdfMagic(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> magic = "%PDF-"u8;
        return span[..Math.Min(span.Length, 1024)].IndexOf(magic) >= 0;
    }

    /// <summary>Finish + status for a reader that produced text. capped = the reader hit any of its own caps.</summary>
    internal static ExtractedText Build(string raw, bool capped)
    {
        var (text, cut) = Finish(raw);
        return cut || capped
            ? new ExtractedText(ExtractStatus.Truncated, text, null, true)
            : new ExtractedText(ExtractStatus.Read, text, null, false);
    }

    /// <summary>An attached Outlook item read through COM (SPEC 26.4 "Attached Outlook item", 26.9):
    /// rendered like .eml plus "Attachments: names". Status Read or Truncated. Never throws.</summary>
    public static ExtractedText FromMailItem(string? subject, string? from, DateTimeOffset? sent, string? body,
        IReadOnlyList<string> attachmentNames)
    {
        try
        {
            string? date = sent?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return Build(RenderMessage(subject, from, date, attachmentNames, body ?? ""), false);
        }
        catch (Exception)
        {
            return ExtractedText.Skip(AttachmentReasons.Damaged);
        }
    }

    /// <summary>Normalizes and caps at MaxFileChars. Cut = true when it cut.</summary>
    internal static (string Text, bool Cut) Finish(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return ("", false);

        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < raw.Length && raw[i + 1] == '\n') i++;
            }
            else if (c == '\n' || c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        string[] lines = sb.ToString().Split('\n');
        var outLines = new List<string>(lines.Length);
        int emptyRun = 0;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (outLines.Count == 0) continue;      // leading empty lines
                if (++emptyRun > 2) continue;           // runs > 2 become 2
                outLines.Add("");
            }
            else
            {
                emptyRun = 0;
                outLines.Add(line);
            }
        }
        while (outLines.Count > 0 && outLines[^1].Length == 0) outLines.RemoveAt(outLines.Count - 1);

        string text = string.Join('\n', outLines);
        if (text.Length <= MaxFileChars) return (text, false);

        int keep = MaxFileChars - 1;
        if (char.IsHighSurrogate(text[keep - 1])) keep--;
        return (text[..keep] + "…", true);
    }

    /// <summary>"Subject: ...\nFrom: ...\nDate: ...[\nAttachments: a, b]\n\ntext"; empty/null header lines omitted.</summary>
    internal static string RenderMessage(string? subject, string? from, string? date,
        IReadOnlyList<string>? attachmentNames, string text)
    {
        var sb = new StringBuilder();
        void Line(string label, string? value)
        {
            string v = OneLine(value);
            if (v.Length == 0) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(label).Append(": ").Append(v);
        }

        Line("Subject", subject);
        Line("From", from);
        Line("Date", date);
        if (attachmentNames is not null)
        {
            var names = new List<string>();
            foreach (string n in attachmentNames)
            {
                string one = OneLine(n);
                if (one.Length > 0) names.Add(one);
            }
            if (names.Count > 0) Line("Attachments", string.Join(", ", names));
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static string OneLine(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// <summary>StringBuilder with a cap; once full, further appends are ignored.</summary>
internal sealed class TextSink
{
    private readonly StringBuilder _sb = new();
    private readonly int _limit;

    public TextSink(int limit = AttachmentText.MaxFileChars * 2) => _limit = limit;

    public bool Full => _sb.Length >= _limit;

    public void Append(string? s)
    {
        if (string.IsNullOrEmpty(s) || Full) return;
        int room = _limit - _sb.Length;
        if (s.Length <= room) _sb.Append(s);
        else _sb.Append(s, 0, room);
    }

    public void Append(char c)
    {
        if (!Full) _sb.Append(c);
    }

    public override string ToString() => _sb.ToString();
}

internal static class PlainText
{
    /// <summary>First MaxTextBytes (backed off to a UTF-8 / UTF-16 boundary), Signatures.Decode.
    /// truncated = bytes were left over. Returns null when the decoded text contains NUL.</summary>
    public static string? Decode(ReadOnlySpan<byte> bytes, out bool truncated)
    {
        int len = bytes.Length;
        int n = Math.Min(len, AttachmentText.MaxTextBytes);
        truncated = n < len;
        if (truncated)
        {
            bool utf16 = len >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
            if (utf16)
            {
                n &= ~1;
            }
            else
            {
                int steps = 0;
                while (n > 0 && steps < 3 && bytes[n] is >= 0x80 and <= 0xBF)
                {
                    n--;
                    steps++;
                }
            }
        }

        string text = Signatures.Decode(bytes[..n].ToArray());
        return text.Contains('\0') ? null : text;
    }
}
