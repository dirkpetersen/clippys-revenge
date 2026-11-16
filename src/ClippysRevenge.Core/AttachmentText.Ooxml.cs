using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace ClippysRevenge.Core;

/// <summary>Thrown by <see cref="LimitedReadStream"/>. The message never carries file data.</summary>
internal sealed class ExtractLimitException() : Exception("limit")
{
}

/// <summary>Read-only wrapper; throws ExtractLimitException once more than <c>limit</c> bytes were read.</summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        long remaining = limit - _total;
        if (remaining <= 0)
        {
            // Limit reached: one more byte means the stream is longer than allowed.
            Span<byte> probe = stackalloc byte[1];
            if (inner.Read(probe) > 0) throw new ExtractLimitException();
            return 0;
        }

        int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
        _total += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Own reader for docx/xlsx/pptx (SPEC 26.4): zip-bomb limits, counting stream, DTDs prohibited, only whitelisted
/// parts reached through whitelisted relationships are opened. Macros, embeddings, media, external targets and
/// field codes are never opened or followed.
/// </summary>
internal static class OoxmlText
{
    public const int MaxEntries = 2_000;
    public const long MaxDeclaredTotal = 100L * 1024 * 1024;
    public const long RatioCheckMinBytes = 1024 * 1024;
    public const int MaxRatio = 200;
    public const long MaxPartBytes = 20L * 1024 * 1024;
    public const long MaxXmlChars = 20_000_000;

    private static readonly string[] W =
        ["http://schemas.openxmlformats.org/wordprocessingml/2006/main", "http://purl.oclc.org/ooxml/wordprocessingml/main"];
    private static readonly string[] X =
        ["http://schemas.openxmlformats.org/spreadsheetml/2006/main", "http://purl.oclc.org/ooxml/spreadsheetml/main"];
    private static readonly string[] P =
        ["http://schemas.openxmlformats.org/presentationml/2006/main", "http://purl.oclc.org/ooxml/presentationml/main"];
    private static readonly string[] A =
        ["http://schemas.openxmlformats.org/drawingml/2006/main", "http://purl.oclc.org/ooxml/drawingml/main"];
    private static readonly string[] R =
        ["http://schemas.openxmlformats.org/officeDocument/2006/relationships", "http://purl.oclc.org/ooxml/officeDocument/relationships"];
    private const string McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private sealed record Rel(string Id, string Type, string Target);

    /// <param name="openedParts">Test hook: receives the entry name of every part opened. Null in production.</param>
    public static ExtractedText Extract(AttachmentFormat format, ReadOnlyMemory<byte> bytes, CancellationToken ct,
        ICollection<string>? openedParts = null)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!EndOfDirectoryOk(bytes.Span)) return ExtractedText.Skip(AttachmentReasons.Damaged);

            Stream ms = MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> seg) && seg.Array is not null
                ? new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false)
                : new MemoryStream(bytes.ToArray(), writable: false);
            using Stream msOwner = ms;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            if (!ZipLimitsOk(zip)) return ExtractedText.Skip(AttachmentReasons.Damaged);

            var pkg = new Package(zip, openedParts, ct);
            var sink = new TextSink();
            bool capped = format switch
            {
                AttachmentFormat.Word => ReadWord(pkg, sink, ct),
                AttachmentFormat.Excel => ReadExcel(pkg, sink, ct),
                AttachmentFormat.PowerPoint => ReadPowerPoint(pkg, sink, ct),
                _ => throw new InvalidDataException(),
            };
            return AttachmentText.Build(sink.ToString(), capped || sink.Full);
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

    // ------------------------------------------------------------------ zip pre-checks

    /// <summary>End of Central Directory entry count, before ZipArchive materializes the directory.</summary>
    private static bool EndOfDirectoryOk(ReadOnlySpan<byte> z)
    {
        int start = Math.Max(0, z.Length - 65_557);
        for (int i = z.Length - 22; i >= start; i--)
        {
            if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 0x05 && z[i + 3] == 0x06)
            {
                int total = z[i + 10] | (z[i + 11] << 8);
                return total != 0xFFFF && total <= MaxEntries;
            }
        }
        return false;
    }

    private static bool ZipLimitsOk(ZipArchive zip)
    {
        var entries = zip.Entries;
        if (entries.Count > MaxEntries) return false;
        long sum = 0;
        foreach (ZipArchiveEntry e in entries)
        {
            long len = e.Length;
            sum += len;
            if (sum > MaxDeclaredTotal) return false;
            if (len > RatioCheckMinBytes)
            {
                long comp = e.CompressedLength;
                if (comp <= 0 || len / comp > MaxRatio) return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ package access

    private sealed class Package
    {
        private readonly Dictionary<string, ZipArchiveEntry> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly ICollection<string>? _opened;
        private readonly CancellationToken _ct;

        public Package(ZipArchive zip, ICollection<string>? opened, CancellationToken ct)
        {
            _opened = opened;
            _ct = ct;
            foreach (ZipArchiveEntry e in zip.Entries) _map.TryAdd(Norm(e.FullName), e);
        }

        public static string Norm(string name) => name.Replace('\\', '/').TrimStart('/');

        /// <summary>Only .xml and .rels parts are ever opened. Null when absent or not allowed.</summary>
        public XmlReader? Open(string name)
        {
            string n = Norm(name);
            if (!(n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (!_map.TryGetValue(n, out ZipArchiveEntry? entry)) return null;

            _ct.ThrowIfCancellationRequested();
            _opened?.Add(entry.FullName);
            var stream = new LimitedReadStream(entry.Open(), MaxPartBytes);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxXmlChars,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true,
            };
            return XmlReader.Create(stream, settings);
        }

        public XmlReader OpenRequired(string name) => Open(name) ?? throw new InvalidDataException();

        /// <summary>The relationships of a part, or null when it has no rels part. External and odd targets are dropped.</summary>
        public List<Rel>? Rels(string sourcePart)
        {
            string src = Norm(sourcePart);
            int slash = src.LastIndexOf('/');
            string dir = slash < 0 ? "" : src[..(slash + 1)];
            string file = slash < 0 ? src : src[(slash + 1)..];
            using XmlReader? r = Open(dir + "_rels/" + file + ".rels");
            if (r is null) return null;

            var list = new List<Rel>();
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
                string? id = r.GetAttribute("Id");
                string? type = r.GetAttribute("Type");
                string? target = r.GetAttribute("Target");
                string? mode = r.GetAttribute("TargetMode");
                if (id is null || type is null || target is null) continue;
                if (string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase)) continue;
                if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                string? resolved = ResolveTarget(dir, target);
                if (resolved is null) continue;
                list.Add(new Rel(id, type, resolved));
            }
            return list;
        }

        private static string? ResolveTarget(string sourceDir, string target)
        {
            string t = Uri.UnescapeDataString(target).Replace('\\', '/');
            var segs = new List<string>();
            if (!t.StartsWith('/'))
            {
                foreach (string s in sourceDir.Split('/', StringSplitOptions.RemoveEmptyEntries)) segs.Add(s);
            }
            foreach (string s in t.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (s == ".") continue;
                if (s == "..")
                {
                    if (segs.Count == 0) return null;
                    segs.RemoveAt(segs.Count - 1);
                    continue;
                }
                segs.Add(s);
            }
            return segs.Count == 0 ? null : string.Join('/', segs);
        }
    }

    // ------------------------------------------------------------------ element helpers

    private static bool Is(XmlReader r, string[] ns, string local) =>
        r.LocalName == local && Array.IndexOf(ns, r.NamespaceURI) >= 0;

    private static bool IsFallback(XmlReader r) => r.LocalName == "Fallback" && r.NamespaceURI == McNs;

    private static string? RelId(XmlReader r)
    {
        foreach (string ns in R)
        {
            string? v = r.GetAttribute("id", ns);
            if (v is not null) return v;
        }
        return null;
    }

    private static Rel? FirstOfType(List<Rel>? rels, string typeSuffix) =>
        rels?.FirstOrDefault(x => x.Type.EndsWith(typeSuffix, StringComparison.Ordinal));

    private static bool IsTextNode(XmlNodeType t) =>
        t is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

    // ------------------------------------------------------------------ Word

    private static bool ReadWord(Package pkg, TextSink sink, CancellationToken ct)
    {
        using XmlReader r = pkg.OpenRequired("word/document.xml");
        bool inT = false, pendingSpace = false, pendingTab = false;
        int cellDepth = 0, pPr = 0, nodes = 0;

        void Add(string s)
        {
            if (pendingSpace)
            {
                sink.Append(' ');
                pendingSpace = false;
            }
            sink.Append(s);
        }

        void EndP()
        {
            if (cellDepth > 0) pendingSpace = true;
            else sink.Append('\n');
        }

        void EndTc()
        {
            if (cellDepth > 0) cellDepth--;
            pendingSpace = false;
            pendingTab = true;
        }

        void EndTr()
        {
            pendingTab = false;
            sink.Append('\n');
        }

        r.Read();
        while (!r.EOF && !sink.Full)
        {
            if ((++nodes & 1023) == 0) ct.ThrowIfCancellationRequested();
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    if (IsFallback(r))
                    {
                        r.Skip();
                        continue;
                    }
                    bool empty = r.IsEmptyElement;
                    if (Is(r, W, "t")) inT = !empty;
                    else if (Is(r, W, "tab")) { if (pPr == 0) Add("\t"); }
                    else if (Is(r, W, "br") || Is(r, W, "cr")) Add("\n");
                    else if (Is(r, W, "pPr")) { if (!empty) pPr++; }
                    else if (Is(r, W, "tc"))
                    {
                        if (pendingTab)
                        {
                            sink.Append('\t');
                            pendingTab = false;
                        }
                        if (empty) EndTc();
                        else cellDepth++;
                    }
                    else if (Is(r, W, "tr")) { if (empty) EndTr(); }
                    else if (Is(r, W, "p")) { if (empty) EndP(); }
                    break;

                case XmlNodeType.EndElement:
                    if (Is(r, W, "t")) inT = false;
                    else if (Is(r, W, "pPr")) { if (pPr > 0) pPr--; }
                    else if (Is(r, W, "tc")) EndTc();
                    else if (Is(r, W, "tr")) EndTr();
                    else if (Is(r, W, "p")) EndP();
                    break;

                default:
                    if (inT && IsTextNode(r.NodeType)) Add(r.Value);
                    break;
            }
            r.Read();
        }
        return sink.Full;
    }

    // ------------------------------------------------------------------ Excel

    private sealed class SheetContext(bool date1904, List<string> shared, bool[] dateXf)
    {
        public bool Date1904 { get; } = date1904;
        public List<string> Shared { get; } = shared;
        public bool[] DateXf { get; } = dateXf;
    }

    private static bool ReadExcel(Package pkg, TextSink sink, CancellationToken ct)
    {
        bool date1904 = false;
        var sheets = new List<(string Name, string? RId)>();
        using (XmlReader r = pkg.OpenRequired("xl/workbook.xml"))
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (Is(r, X, "workbookPr"))
                {
                    string? d = r.GetAttribute("date1904");
                    date1904 = d is "1" || string.Equals(d, "true", StringComparison.OrdinalIgnoreCase);
                }
                else if (Is(r, X, "sheet"))
                {
                    sheets.Add((r.GetAttribute("name") ?? "", RelId(r)));
                }
            }
        }

        List<Rel> rels = pkg.Rels("xl/workbook.xml") ?? throw new InvalidDataException();

        var shared = new List<string>();
        if (FirstOfType(rels, "/sharedStrings") is { } sstRel) ReadSharedStrings(pkg, sstRel.Target, shared, ct);
        bool[] dateXf = FirstOfType(rels, "/styles") is { } stRel ? ReadStyles(pkg, stRel.Target) : [];
        var ctx = new SheetContext(date1904, shared, dateXf);

        bool capped = false;
        int done = 0;
        foreach (var (name, rid) in sheets)
        {
            ct.ThrowIfCancellationRequested();
            Rel? rel = rid is null ? null : rels.FirstOrDefault(x => x.Id == rid);
            if (rel is null || !rel.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;
            if (done == AttachmentText.MaxSheets)
            {
                capped = true;
                break;
            }
            if (sink.Full)
            {
                capped = true;
                break;
            }
            if (done > 0) sink.Append("\n\n");
            sink.Append("## ");
            sink.Append(Flatten(name));
            if (ReadSheet(pkg, rel.Target, ctx, sink, ct)) capped = true;
            done++;
        }
        return capped;
    }

    private static string Flatten(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string CapCell(string s) =>
        s.Length > AttachmentText.MaxCellChars ? s[..AttachmentText.MaxCellChars] : s;

    /// <summary>Reads an si / is element (positioned on it) and moves past its end. Phonetic runs are skipped.</summary>
    private static string ReadRichText(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return "";
        }
        var sb = new StringBuilder();
        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (Is(r, X, "rPh"))
                {
                    r.Skip();
                    continue;
                }
                if (Is(r, X, "t"))
                {
                    sb.Append(r.ReadElementContentAsString());
                    continue;
                }
            }
            r.Read();
        }
        r.Read();
        return sb.ToString();
    }

    private static void ReadSharedStrings(Package pkg, string part, List<string> into, CancellationToken ct)
    {
        using XmlReader? r = pkg.Open(part);
        if (r is null) return;
        r.Read();
        int n = 0;
        while (!r.EOF)
        {
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (r.NodeType == XmlNodeType.Element && Is(r, X, "si"))
            {
                into.Add(CapCell(ReadRichText(r)));
                continue;
            }
            r.Read();
        }
    }

    private static bool[] ReadStyles(Package pkg, string part)
    {
        using XmlReader? r = pkg.Open(part);
        if (r is null) return [];

        var custom = new Dictionary<int, string>();
        var xfIds = new List<int>();
        bool inCellXfs = false;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (Is(r, X, "numFmt"))
                {
                    if (int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                        custom[id] = r.GetAttribute("formatCode") ?? "";
                }
                else if (Is(r, X, "cellXfs")) inCellXfs = !r.IsEmptyElement;
                else if (inCellXfs && Is(r, X, "xf"))
                {
                    int.TryParse(r.GetAttribute("numFmtId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id);
                    xfIds.Add(id);
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && Is(r, X, "cellXfs")) inCellXfs = false;
        }

        var result = new bool[xfIds.Count];
        for (int i = 0; i < result.Length; i++) result[i] = IsDateFormat(xfIds[i], custom);
        return result;
    }

    private static bool IsDateFormat(int id, Dictionary<int, string> custom)
    {
        if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
        return custom.TryGetValue(id, out string? code) && CodeHasDate(code);
    }

    private static bool CodeHasDate(string code)
    {
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '"')
            {
                int close = code.IndexOf('"', i + 1);
                if (close < 0) return false;
                i = close;
            }
            else if (c == '[')
            {
                int close = code.IndexOf(']', i + 1);
                if (close < 0) return false;
                i = close;
            }
            else if (c == '\\')
            {
                i++;
            }
            else if (c is 'y' or 'Y' or 'd' or 'D')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Appends the sheet's rows to the sink. True when anything was cut off.</summary>
    private static bool ReadSheet(Package pkg, string part, SheetContext ctx, TextSink sink, CancellationToken ct)
    {
        using XmlReader r = pkg.OpenRequired(part);
        bool capped = false;
        bool inSheetData = false;
        int rows = 0, seen = 0;

        r.Read();
        while (!r.EOF && !sink.Full)
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (Is(r, X, "sheetData"))
                {
                    inSheetData = !r.IsEmptyElement;
                }
                else if (inSheetData && Is(r, X, "row"))
                {
                    if ((++seen % 50) == 0) ct.ThrowIfCancellationRequested();
                    string? line = ReadRow(r, ctx, ref capped);
                    if (line is not null)
                    {
                        if (rows == AttachmentText.MaxSheetRows)
                        {
                            capped = true;
                            break;
                        }
                        rows++;
                        sink.Append('\n');
                        sink.Append(line);
                    }
                    continue;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && Is(r, X, "sheetData"))
            {
                inSheetData = false;
            }
            r.Read();
        }
        return capped || sink.Full;
    }

    /// <summary>Reads one row (positioned on it, moves past it). Null when the row has no non-empty cell.</summary>
    private static string? ReadRow(XmlReader r, SheetContext ctx, ref bool capped)
    {
        if (r.IsEmptyElement)
        {
            r.Read();
            return null;
        }

        var fields = new List<string>();
        int prevCol = 0;
        int depth = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (Is(r, X, "c"))
                {
                    string text = ReadCell(r, ctx, ref prevCol, out int col);
                    if (col > AttachmentText.MaxSheetColumns)
                    {
                        if (text.Length > 0) capped = true;
                    }
                    else
                    {
                        while (fields.Count < col) fields.Add("");
                        fields[col - 1] = text;
                    }
                }
                else r.Skip();
                continue;
            }
            r.Read();
        }
        r.Read();

        int last = fields.Count;
        while (last > 0 && fields[last - 1].Length == 0) last--;
        if (last == 0) return null;
        return string.Join('\t', fields.Take(last));
    }

    /// <summary>Reads one cell (positioned on it, moves past it) and gives its display text.</summary>
    private static string ReadCell(XmlReader r, SheetContext ctx, ref int prevCol, out int col)
    {
        string? cellRef = r.GetAttribute("r");
        string? type = r.GetAttribute("t");
        int style = int.TryParse(r.GetAttribute("s"), NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s : -1;
        col = ColumnOf(cellRef, prevCol);
        prevCol = col;

        string? v = null, inline = null;
        if (r.IsEmptyElement)
        {
            r.Read();
        }
        else
        {
            int depth = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (Is(r, X, "v")) v = r.ReadElementContentAsString();
                    else if (Is(r, X, "is")) inline = ReadRichText(r);
                    else r.Skip();   // f (formula) is never read; its cached v is the value
                    continue;
                }
                r.Read();
            }
            r.Read();
        }

        string text;
        switch (type)
        {
            case "s":
                text = int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx < ctx.Shared.Count
                    ? ctx.Shared[idx]
                    : "";
                break;
            case "inlineStr":
                text = inline ?? "";
                break;
            case "str":
            case "e":
            case "d":
                text = v ?? "";
                break;
            case "b":
                text = v switch { "1" => "TRUE", "0" => "FALSE", _ => v ?? "" };
                break;
            default:
                text = v ?? "";
                if (text.Length > 0 && style >= 0 && style < ctx.DateXf.Length && ctx.DateXf[style]
                    && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && d >= 0 && d < 2958466)
                {
                    text = FormatDate(d + (ctx.Date1904 ? 1462 : 0));
                }
                break;
        }

        if (text.Length == 0) return "";
        return CapCell(text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
    }

    private static string FormatDate(double serial)
    {
        DateTime dt = DateTime.FromOADate(serial);
        long ticks = (dt.Ticks + TimeSpan.TicksPerSecond * 30) / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
        dt = new DateTime(ticks);
        return dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static int ColumnOf(string? cellRef, int prevCol)
    {
        int col = 0, letters = 0;
        if (cellRef is not null)
        {
            foreach (char ch in cellRef)
            {
                char u = char.ToUpperInvariant(ch);
                if (u is < 'A' or > 'Z') break;
                col = Math.Min(col * 26 + (u - 'A' + 1), 1_000_000);
                letters++;
            }
        }
        return letters == 0 ? prevCol + 1 : col;
    }

    // ------------------------------------------------------------------ PowerPoint

    private static bool ReadPowerPoint(Package pkg, TextSink sink, CancellationToken ct)
    {
        var slideIds = new List<string>();
        using (XmlReader r = pkg.OpenRequired("ppt/presentation.xml"))
        {
            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.Element && Is(r, P, "sldId") && RelId(r) is { } id) slideIds.Add(id);
            }
        }

        List<Rel> rels = pkg.Rels("ppt/presentation.xml") ?? throw new InvalidDataException();
        bool capped = false;
        int done = 0;
        foreach (string id in slideIds)
        {
            ct.ThrowIfCancellationRequested();
            Rel? rel = rels.FirstOrDefault(x => x.Id == id);
            if (rel is null || !rel.Type.EndsWith("/slide", StringComparison.Ordinal)) continue;
            if (done == AttachmentText.MaxSlides || sink.Full)
            {
                capped = true;
                break;
            }

            var lines = new List<string>();
            using (XmlReader r = pkg.OpenRequired(rel.Target)) ReadParagraphs(r, lines, ct);

            var notes = new List<string>();
            Rel? notesRel = FirstOfType(pkg.Rels(rel.Target), "/notesSlide");
            if (notesRel is not null)
            {
                using XmlReader? nr = pkg.Open(notesRel.Target);
                if (nr is not null) ReadParagraphs(nr, notes, ct);
            }

            done++;
            if (done > 1) sink.Append("\n\n");
            sink.Append("## Slide ");
            sink.Append(done.ToString(CultureInfo.InvariantCulture));
            foreach (string line in lines)
            {
                sink.Append('\n');
                sink.Append(line);
            }
            if (notes.Count > 0)
            {
                sink.Append("\nNotes:");
                foreach (string line in notes)
                {
                    sink.Append('\n');
                    sink.Append(line);
                }
            }
        }
        return capped;
    }

    /// <summary>Each non-empty a:p as one line (a:t texts, a:br as newline). a:fld and mc:Fallback subtrees are skipped.</summary>
    private static void ReadParagraphs(XmlReader r, List<string> lines, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int total = 0, nodes = 0;
        r.Read();
        while (!r.EOF && total <= AttachmentText.MaxFileChars * 2)
        {
            if ((++nodes & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (r.NodeType == XmlNodeType.Element)
            {
                if (IsFallback(r) || Is(r, A, "fld"))
                {
                    r.Skip();
                    continue;
                }
                if (Is(r, A, "p")) sb.Clear();
                else if (Is(r, A, "br")) sb.Append('\n');
                else if (Is(r, A, "t"))
                {
                    sb.Append(r.ReadElementContentAsString());
                    continue;
                }
            }
            else if (r.NodeType == XmlNodeType.EndElement && Is(r, A, "p"))
            {
                string line = sb.ToString();
                sb.Clear();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(line);
                    total += line.Length;
                }
            }
            r.Read();
        }
    }
}
