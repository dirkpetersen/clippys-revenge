using static ClippysRevenge.Core.Tests.OoxmlFixtures;

namespace ClippysRevenge.Core.Tests;

public class OoxmlTextTests
{
    private static ExtractedText Run(string name, byte[] bytes) =>
        AttachmentText.Extract(name, bytes, CancellationToken.None);

    private static void AssertDamaged(ExtractedText r)
    {
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal("the file seems damaged", r.Reason);
    }

    // ------------------------------------------------------------------ docx

    private const string FirstCase =
        "<w:p><w:r><w:t>One</w:t></w:r></w:p><w:p><w:r><w:t>Two</w:t><w:tab/><w:t>Three</w:t><w:br/><w:t>Four</w:t></w:r></w:p>";

    [Fact]
    public void Docx_paragraphs()
    {
        var r = Run("a.docx", Docx(FirstCase));
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Equal("One\nTwo\tThree\nFour", r.Text);
    }

    [Fact]
    public void Docx_strict_namespace()
    {
        Assert.Equal("One\nTwo\tThree\nFour", Run("a.docx", Docx(FirstCase, WNsStrict)).Text);
    }

    [Fact]
    public void Docx_tab_stops_in_paragraph_properties_are_not_tabs()
    {
        var r = Run("a.docx", Docx("<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"100\"/></w:tabs></w:pPr><w:r><w:t>X</w:t></w:r></w:p>"));
        Assert.Equal("X", r.Text);
    }

    [Fact]
    public void Docx_table()
    {
        string tc(string paras) => $"<w:tc>{paras}</w:tc>";
        string body = "<w:tbl>"
            + "<w:tr>" + tc(P("a")) + tc(P("b")) + "</w:tr>"
            + "<w:tr>" + tc(P("c")) + tc(P("x") + P("y")) + "</w:tr>"
            + "</w:tbl>";
        var r = Run("a.docx", Docx(body));
        Assert.Equal("a\tb\nc\tx y", r.Text);
    }

    [Fact]
    public void Docx_skips_deleted_text_and_field_codes()
    {
        var r = Run("a.docx", Docx("<w:p><w:r><w:t>Keep</w:t></w:r><w:del><w:r><w:delText>GONE</w:delText></w:r></w:del>"
            + "<w:r><w:instrText>HYPERLINK \"http://x\"</w:instrText></w:r></w:p>"));
        Assert.Equal("Keep", r.Text);
    }

    [Fact]
    public void Docx_mc_fallback_is_not_doubled()
    {
        string body = "<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:txbxContent><w:p><w:r><w:t>Box</w:t></w:r></w:p></w:txbxContent></mc:Choice>"
            + "<mc:Fallback><w:txbxContent><w:p><w:r><w:t>Box</w:t></w:r></w:p></w:txbxContent></mc:Fallback></mc:AlternateContent></w:r></w:p>";
        var r = Run("a.docx", Docx(body));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(r.Text, "Box"));
    }

    [Fact]
    public void Docx_does_not_read_notes_headers_footers()
    {
        var r = Run("a.docx", Docx(P("Main"), WNs,
            ("word/footnotes.xml", WordDoc(P("FOOT"))), ("word/header1.xml", WordDoc(P("HEAD")))));
        Assert.Equal("Main", r.Text);
    }

    [Fact]
    public void Docx_missing_document_is_damaged()
    {
        AssertDamaged(Run("a.docx", Zip(("[Content_Types].xml", "<Types/>"))));
    }

    [Fact]
    public void Truncation_by_chars()
    {
        var r = Run("a.docx", Docx(P(new string('x', 50_000))));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.Equal(20_000, r.Text.Length);
    }

    // ------------------------------------------------------------------ xlsx

    private static ExtractedText Sheet(string rows, string[]? shared = null, string? styles = null, bool date1904 = false) =>
        Run("a.xlsx", Xlsx([("Budget", rows)], shared, styles, date1904));

    [Fact]
    public void Xlsx_strings_and_numbers()
    {
        var r = Sheet(Row(1, SharedCell("A1", 0), InlineCell("B1", "Cost")) + Row(2, SharedCell("A2", 1), NumCell("B2", "12.5")),
            ["Item", "Pens"]);
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.Equal("## Budget\nItem\tCost\nPens\t12.5", r.Text);
    }

    [Fact]
    public void Xlsx_gaps_give_empty_fields()
    {
        Assert.Equal("## Budget\nx\t\tz", Sheet(Row(1, InlineCell("A1", "x"), InlineCell("C1", "z"))).Text);
    }

    [Theory]
    [InlineData("45606", 14, "", "2024-11-10")]
    [InlineData("45606.5", 22, "", "2024-11-10 12:00")]
    [InlineData("45606", 164, "<numFmt numFmtId=\"164\" formatCode=\"[$-409]d-mmm-yy;@\"/>", "2024-11-10")]
    [InlineData("3", 165, "<numFmt numFmtId=\"165\" formatCode=\"0.00&quot; days&quot;\"/>", "3")]
    public void Xlsx_dates(string value, int fmt, string numFmts, string expected)
    {
        var r = Sheet(Row(1, NumCell("A1", value, 0)), styles: Styles(numFmts, fmt));
        Assert.Equal("## Budget\n" + expected, r.Text);
    }

    [Fact]
    public void Xlsx_date1904()
    {
        var r = Sheet(Row(1, NumCell("A1", "0", 0)), styles: Styles("", 14), date1904: true);
        Assert.Equal("## Budget\n1904-01-01", r.Text);
    }

    [Fact]
    public void Xlsx_bool_error_formula()
    {
        string cells = "<c r=\"A1\" t=\"b\"><v>1</v></c><c r=\"B1\" t=\"e\"><v>#DIV/0!</v></c><c r=\"C1\"><f>SUM(A1)</f><v>7</v></c>";
        Assert.Equal("## Budget\nTRUE\t#DIV/0!\t7", Sheet(Row(1, cells)).Text);
    }

    [Fact]
    public void Xlsx_cell_is_cut_and_flattened()
    {
        string text = new string('a', 50) + "\t" + new string('b', 50) + "\n" + new string('c', 300);
        string line = Sheet(Row(1, InlineCell("A1", text))).Text.Split('\n')[1];
        Assert.Equal(200, line.Length);
        Assert.DoesNotContain('\t', line);
    }

    [Fact]
    public void Xlsx_row_cap()
    {
        string rows = string.Concat(Enumerable.Range(1, 201).Select(i => Row(i, InlineCell($"A{i}", $"row{i}x"))));
        var r = Sheet(rows);
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.Contains("row200x", r.Text);
        Assert.DoesNotContain("row201x", r.Text);
    }

    [Fact]
    public void Xlsx_empty_rows_are_left_out()
    {
        string rows = Row(1, InlineCell("A1", "a")) + Row(2) + Row(5, InlineCell("A5", "b")) + Row(9, InlineCell("A9", "c"));
        Assert.Equal("## Budget\na\nb\nc", Sheet(rows).Text);
    }

    [Fact]
    public void Xlsx_column_cap()
    {
        string cells = string.Concat(Enumerable.Range(0, 31).Select(i =>
            InlineCell((i < 26 ? ((char)('A' + i)).ToString() : "A" + (char)('A' + i - 26)) + "1", $"v{i}")));
        var r = Sheet(Row(1, cells));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.Equal(30, r.Text.Split('\n')[1].Split('\t').Length);
    }

    [Fact]
    public void Xlsx_sheet_cap()
    {
        var sheets = Enumerable.Range(1, 6).Select(i => ($"S{i}", Row(1, InlineCell("A1", "v")))).ToArray();
        var r = Run("a.xlsx", Xlsx(sheets));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.Contains("## S5", r.Text);
        Assert.DoesNotContain("## S6", r.Text);
    }

    [Fact]
    public void Xlsx_sheet_order_follows_the_workbook()
    {
        var r = Run("a.xlsx", Xlsx([("Zeta", Row(1, InlineCell("A1", "z"))), ("Alpha", Row(1, InlineCell("A1", "a")))],
            reverseRels: true));
        Assert.True(r.Text.IndexOf("## Zeta", StringComparison.Ordinal) < r.Text.IndexOf("## Alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void Xlsx_chartsheets_do_not_count()
    {
        var sheets = Enumerable.Range(1, 5).Select(i => ($"S{i}", Row(1, InlineCell("A1", "v")))).ToArray();
        var r = Run("a.xlsx", Xlsx(sheets, extraRels: $"<Relationship Id=\"rId50\" Type=\"{OdRel}/chartsheet\" Target=\"chartsheets/sheet1.xml\"/>",
            extraSheets: "<sheet name=\"Chart\" sheetId=\"50\" r:id=\"rId50\"/>"));
        Assert.Equal(ExtractStatus.Read, r.Status);
    }

    // ------------------------------------------------------------------ pptx

    [Fact]
    public void Pptx_slides_and_notes()
    {
        var r = Run("a.pptx", Pptx([(A("Title one"), A("Say hi")), (A("Second"), null)]));
        Assert.Equal("## Slide 1\nTitle one\nNotes:\nSay hi\n\n## Slide 2\nSecond", r.Text);
    }

    [Fact]
    public void Pptx_follows_sldIdLst_order()
    {
        var r = Run("a.pptx", Pptx([(A("First file"), null), (A("Second file"), null)], reverseOrder: true));
        Assert.Equal("## Slide 1\nSecond file\n\n## Slide 2\nFirst file", r.Text);
    }

    [Fact]
    public void Pptx_slide_number_field_is_skipped()
    {
        string notes = A("Words") + "<a:p><a:fld type=\"slidenum\"><a:t>3</a:t></a:fld></a:p>";
        var r = Run("a.pptx", Pptx([(A("S"), notes)]));
        Assert.Equal("## Slide 1\nS\nNotes:\nWords", r.Text);
    }

    [Fact]
    public void Pptx_slide_cap()
    {
        var slides = Enumerable.Range(1, 61).Select(i => (A($"text{i}"), (string?)null)).ToArray();
        var r = Run("a.pptx", Pptx(slides));
        Assert.Equal(ExtractStatus.Truncated, r.Status);
        Assert.Contains("## Slide 60", r.Text);
        Assert.DoesNotContain("## Slide 61", r.Text);
    }
}
