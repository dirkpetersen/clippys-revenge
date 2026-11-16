using System.IO.Compression;
using System.Text;

namespace ClippysRevenge.Core.Tests;

/// <summary>Builds docx/xlsx/pptx/zip byte arrays in memory. Nothing binary is checked in.</summary>
internal static class OoxmlFixtures
{
    public const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string WNsStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    public const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string OdRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string XNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    public static byte[] Zip(params (string Name, string Content)[] parts) =>
        Zip(parts.Select(p => (p.Name, Encoding.UTF8.GetBytes(p.Content), CompressionLevel.Optimal)).ToArray());

    public static byte[] Zip(params (string Name, byte[] Content, CompressionLevel Level)[] parts)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content, level) in parts)
            {
                var e = zip.CreateEntry(name, level);
                using var s = e.Open();
                s.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static List<(string Name, string Content)> Base(string mainPart, string mainRels, string? mainRelsFor = null) =>
    [
        ("[Content_Types].xml", Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("_rels/.rels", Decl + $"<Relationships xmlns=\"{RelNs}\"><Relationship Id=\"rId1\" Type=\"{OdRel}/officeDocument\" Target=\"{mainPart}\"/></Relationships>"),
    ];

    public static string Rels(params (string Id, string Type, string Target, string? Mode)[] rels) =>
        Decl + $"<Relationships xmlns=\"{RelNs}\">"
        + string.Concat(rels.Select(r =>
            $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{r.Target}\""
            + (r.Mode is null ? "" : $" TargetMode=\"{r.Mode}\"") + "/>"))
        + "</Relationships>";

    // ------------------------------------------------------------------ Word

    public static string WordDoc(string bodyXml, string wNs = WNs) =>
        Decl + $"<w:document xmlns:w=\"{wNs}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" "
        + "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\">"
        + $"<w:body>{bodyXml}</w:body></w:document>";

    public static byte[] Docx(string bodyXml, string wNs = WNs, params (string Name, string Content)[] extra)
    {
        var parts = Base("word/document.xml", "");
        parts.Add(("word/document.xml", WordDoc(bodyXml, wNs)));
        parts.AddRange(extra);
        return Zip(parts.ToArray());
    }

    public static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    // ------------------------------------------------------------------ Excel

    public static string Row(int r, params string[] cells) => $"<row r=\"{r}\">{string.Concat(cells)}</row>";
    public static string NumCell(string cellRef, string v, int? style = null) =>
        $"<c r=\"{cellRef}\"{(style is null ? "" : $" s=\"{style}\"")}><v>{v}</v></c>";
    public static string SharedCell(string cellRef, int idx) => $"<c r=\"{cellRef}\" t=\"s\"><v>{idx}</v></c>";
    public static string InlineCell(string cellRef, string text) =>
        $"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";

    public static string Styles(string numFmts, params int[] xfNumFmtIds) =>
        Decl + $"<styleSheet xmlns=\"{XNs}\">"
        + (numFmts.Length == 0 ? "" : $"<numFmts>{numFmts}</numFmts>")
        + "<cellStyleXfs><xf numFmtId=\"14\"/></cellStyleXfs>"
        + $"<cellXfs>{string.Concat(xfNumFmtIds.Select(i => $"<xf numFmtId=\"{i}\"/>"))}</cellXfs></styleSheet>";

    public static byte[] Xlsx((string Name, string Rows)[] sheets, string[]? shared = null, string? styles = null,
        bool date1904 = false, bool reverseRels = false, string extraRels = "", string extraSheets = "",
        params (string Name, string Content)[] extraParts)
    {
        var parts = Base("xl/workbook.xml", "");
        var sheetEls = new StringBuilder();
        var rels = new List<string>();
        for (int i = 0; i < sheets.Length; i++)
        {
            sheetEls.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            rels.Add($"<Relationship Id=\"rId{i + 1}\" Type=\"{OdRel}/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            parts.Add(($"xl/worksheets/sheet{i + 1}.xml",
                Decl + $"<worksheet xmlns=\"{XNs}\"><sheetData>{sheets[i].Rows}</sheetData></worksheet>"));
        }
        if (shared is not null)
        {
            rels.Add($"<Relationship Id=\"rId100\" Type=\"{OdRel}/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            parts.Add(("xl/sharedStrings.xml", Decl + $"<sst xmlns=\"{XNs}\">"
                + string.Concat(shared.Select(s => $"<si><t>{s}</t></si>")) + "</sst>"));
        }
        if (styles is not null)
        {
            rels.Add($"<Relationship Id=\"rId101\" Type=\"{OdRel}/styles\" Target=\"styles.xml\"/>");
            parts.Add(("xl/styles.xml", styles));
        }
        if (reverseRels) rels.Reverse();
        parts.Add(("xl/workbook.xml", Decl
            + $"<workbook xmlns=\"{XNs}\" xmlns:r=\"{OdRel}\"><workbookPr{(date1904 ? " date1904=\"1\"" : "")}/>"
            + $"<sheets>{sheetEls}{extraSheets}</sheets></workbook>"));
        parts.Add(("xl/_rels/workbook.xml.rels", Decl + $"<Relationships xmlns=\"{RelNs}\">{string.Concat(rels)}{extraRels}</Relationships>"));
        parts.AddRange(extraParts);
        return Zip(parts.ToArray());
    }

    // ------------------------------------------------------------------ PowerPoint

    public static string A(string text) => $"<a:p><a:r><a:t>{text}</a:t></a:r></a:p>";

    private static string SlideXml(string parasXml) =>
        Decl + $"<p:sld xmlns:p=\"{PNs}\" xmlns:a=\"{ANs}\"><p:cSld><p:spTree><p:sp><p:txBody>{parasXml}</p:txBody></p:sp></p:spTree></p:cSld></p:sld>";

    /// <summary>Each slide is the a:p XML of its text; notes (when not null) the a:p XML of the notes page.</summary>
    public static byte[] Pptx((string Paras, string? Notes)[] slides, bool reverseOrder = false,
        params (string Name, string Content)[] extraParts)
    {
        var parts = Base("ppt/presentation.xml", "");
        var ids = new List<string>();
        var rels = new List<string>();
        for (int i = 0; i < slides.Length; i++)
        {
            int n = i + 1;
            ids.Add($"<p:sldId id=\"{255 + n}\" r:id=\"rId{n}\"/>");
            rels.Add($"<Relationship Id=\"rId{n}\" Type=\"{OdRel}/slide\" Target=\"slides/slide{n}.xml\"/>");
            parts.Add(($"ppt/slides/slide{n}.xml", SlideXml(slides[i].Paras)));
            if (slides[i].Notes is { } notes)
            {
                parts.Add(($"ppt/slides/_rels/slide{n}.xml.rels",
                    Rels(("rId1", OdRel + "/notesSlide", $"../notesSlides/notesSlide{n}.xml", null))));
                parts.Add(($"ppt/notesSlides/notesSlide{n}.xml", SlideXml(notes)));
            }
        }
        if (reverseOrder) ids.Reverse();
        parts.Add(("ppt/presentation.xml", Decl + $"<p:presentation xmlns:p=\"{PNs}\" xmlns:r=\"{OdRel}\">"
            + $"<p:sldIdLst>{string.Concat(ids)}</p:sldIdLst></p:presentation>"));
        parts.Add(("ppt/_rels/presentation.xml.rels", Decl + $"<Relationships xmlns=\"{RelNs}\">{string.Concat(rels)}</Relationships>"));
        parts.AddRange(extraParts);
        return Zip(parts.ToArray());
    }

    // ------------------------------------------------------------------ patching

    /// <summary>Rewrites the uncompressed-size field of the entry in its local header (offset 22) and its central directory entry (offset 24).</summary>
    public static byte[] PatchDeclaredSize(byte[] zip, string entryName, uint size)
    {
        byte[] z = (byte[])zip.Clone();
        int eocd = -1;
        for (int i = z.Length - 22; i >= 0; i--)
            if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 0x05 && z[i + 3] == 0x06) { eocd = i; break; }
        if (eocd < 0) throw new InvalidOperationException("no EOCD");
        int count = BitConverter.ToUInt16(z, eocd + 10);
        int pos = (int)BitConverter.ToUInt32(z, eocd + 16);
        for (int n = 0; n < count; n++)
        {
            int nameLen = BitConverter.ToUInt16(z, pos + 28);
            int extraLen = BitConverter.ToUInt16(z, pos + 30);
            int commentLen = BitConverter.ToUInt16(z, pos + 32);
            string name = Encoding.UTF8.GetString(z, pos + 46, nameLen);
            if (name == entryName)
            {
                BitConverter.GetBytes(size).CopyTo(z, pos + 24);
                int local = (int)BitConverter.ToUInt32(z, pos + 42);
                BitConverter.GetBytes(size).CopyTo(z, local + 22);
                return z;
            }
            pos += 46 + nameLen + extraLen + commentLen;
        }
        throw new InvalidOperationException("entry not found");
    }
}
