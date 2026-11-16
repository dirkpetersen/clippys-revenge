using System.IO.Compression;
using System.Text;
using static ClippysRevenge.Core.Tests.OoxmlFixtures;

namespace ClippysRevenge.Core.Tests;

public class OoxmlLimitsTests
{
    private static ExtractedText Run(AttachmentFormat f, byte[] bytes, List<string> opened, CancellationToken? ct = null) =>
        OoxmlText.Extract(f, bytes, ct ?? TestContext.Current.CancellationToken, opened);

    private static void AssertDamaged(ExtractedText r)
    {
        Assert.Equal(ExtractStatus.Skipped, r.Status);
        Assert.Equal("the file seems damaged", r.Reason);
    }

    private static (string, byte[], CompressionLevel) Raw(string name, string content) =>
        (name, Encoding.UTF8.GetBytes(content), CompressionLevel.Optimal);

    private static List<(string, byte[], CompressionLevel)> DocxParts(string bodyXml)
    {
        return
        [
            Raw("[Content_Types].xml", "<Types/>"),
            Raw("word/document.xml", WordDoc(P(bodyXml))),
        ];
    }

    [Fact]
    public void Too_many_entries_is_damaged_and_opens_nothing()
    {
        var parts = DocxParts("x");
        for (int i = 0; i < 2001; i++) parts.Add(Raw($"f/{i}.txt", "a"));
        var opened = new List<string>();
        AssertDamaged(Run(AttachmentFormat.Word, Zip(parts.ToArray()), opened));
        Assert.Empty(opened);
    }

    [Fact]
    public void Exactly_2000_entries_is_read()
    {
        var parts = DocxParts("x");
        for (int i = parts.Count; i < 2000; i++) parts.Add(Raw($"f/{i}.txt", "a"));
        var r = Run(AttachmentFormat.Word, Zip(parts.ToArray()), []);
        Assert.Equal(ExtractStatus.Read, r.Status);
    }

    [Fact]
    public void Declared_total_over_100_mib_is_damaged()
    {
        var parts = DocxParts("x");
        for (int i = 0; i < 3; i++) parts.Add(Raw($"f/{i}.txt", "a"));
        byte[] z = Zip(parts.ToArray());
        for (int i = 0; i < 3; i++) z = PatchDeclaredSize(z, $"f/{i}.txt", 40u * 1024 * 1024);
        var opened = new List<string>();
        AssertDamaged(Run(AttachmentFormat.Word, z, opened));
        Assert.Empty(opened);
    }

    [Fact]
    public void High_ratio_entry_is_damaged_and_opens_nothing()
    {
        var parts = DocxParts("x");
        parts.Add(("xl/worksheets/sheet2.xml", new byte[2 * 1024 * 1024], CompressionLevel.Optimal));
        var opened = new List<string>();
        AssertDamaged(Run(AttachmentFormat.Word, Zip(parts.ToArray()), opened));
        Assert.Empty(opened);
    }

    [Fact]
    public void High_ratio_under_1_mib_is_ignored()
    {
        var parts = DocxParts("x");
        parts.Add(("junk.bin", new byte[900 * 1024], CompressionLevel.Optimal));
        Assert.Equal(ExtractStatus.Read, Run(AttachmentFormat.Word, Zip(parts.ToArray()), []).Status);
    }

    [Fact]
    public void Actual_size_above_declared_is_damaged()
    {
        string big = WordDoc("<w:p><w:r><w:t>" + new string('a', 21 * 1024 * 1024) + "</w:t></w:r></w:p>");
        byte[] z = Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", big));
        z = PatchDeclaredSize(z, "word/document.xml", 100);
        AssertDamaged(Run(AttachmentFormat.Word, z, []));
    }

    [Fact]
    public void Counting_stream_limit()
    {
        using var ok = new LimitedReadStream(new MemoryStream(new byte[100]), 100);
        var buf = new byte[1000];
        int total = 0, n;
        while ((n = ok.Read(buf, 0, buf.Length)) > 0) total += n;
        Assert.Equal(100, total);

        using var over = new LimitedReadStream(new MemoryStream(new byte[101]), 100);
        int read = 0;
        Assert.Throws<ExtractLimitException>(() =>
        {
            int m;
            while ((m = over.Read(buf, 0, buf.Length)) > 0) read += m;
        });
        Assert.Equal(100, read);
    }

    [Fact]
    public void Counting_stream_is_read_only()
    {
        using var s = new LimitedReadStream(new MemoryStream(), 10);
        Assert.False(s.CanWrite);
        Assert.False(s.CanSeek);
        Assert.Throws<NotSupportedException>(() => s.Write(new byte[1], 0, 1));
    }

    [Fact]
    public void Dtd_is_damaged()
    {
        byte[] z = Zip(("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<!DOCTYPE w:document [<!ENTITY x \"boom\">]><w:document xmlns:w=\"" + WNs + "\"><w:body>&x;</w:body></w:document>"));
        AssertDamaged(Run(AttachmentFormat.Word, z, []));
    }

    [Fact]
    public void External_relationship_is_ignored()
    {
        var opened = new List<string>();
        byte[] z = Xlsx([("Real", Row(1, InlineCell("A1", "v")))],
            extraRels: $"<Relationship Id=\"rId77\" Type=\"{OdRel}/worksheet\" Target=\"https://evil.example/x.xml\" TargetMode=\"External\"/>",
            extraSheets: "<sheet name=\"Evil\" sheetId=\"77\" r:id=\"rId77\"/>");
        var r = Run(AttachmentFormat.Excel, z, opened);
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.DoesNotContain(opened, o => o.Contains("evil", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, r.Text.Split('\n').Count(l => l.StartsWith("## ")));
    }

    [Fact]
    public void Climbing_target_is_ignored()
    {
        byte[] z = Pptx([(A("S"), null)], extraParts:
        [
            ("ppt/_rels/presentation.xml.rels", Rels(
                ("rId1", OdRel + "/slide", "slides/slide1.xml", null),
                ("rId2", OdRel + "/slide", "../../../../etc/x.xml", null))),
        ]);
        var r = Run(AttachmentFormat.PowerPoint, z, []);
        Assert.Equal(ExtractStatus.Read, r.Status);
    }

    [Fact]
    public void Macros_are_never_opened()
    {
        byte[] z = Docx(P("Hello"), WNs,
            ("word/vbaProject.bin", "MACRO"),
            ("word/_rels/document.xml.rels", Rels(("rId9", OdRel + "/vbaProject", "vbaProject.bin", null))));
        var opened = new List<string>();
        var r = Run(AttachmentFormat.Word, z, opened);
        Assert.Equal(ExtractStatus.Read, r.Status);
        Assert.DoesNotContain(opened, o => o.Contains("vbaProject", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_xml_and_rels_parts_are_opened()
    {
        byte[] z = Xlsx([("S", Row(1, InlineCell("A1", "v")))], extraParts:
        [
            ("xl/media/image1.png", "PNG"), ("xl/embeddings/oleObject1.bin", "OLE"),
        ]);
        var opened = new List<string>();
        Run(AttachmentFormat.Excel, z, opened);
        Assert.NotEmpty(opened);
        Assert.All(opened, o => Assert.True(o.EndsWith(".xml") || o.EndsWith(".rels")));
        Assert.DoesNotContain(opened, o => o.Contains("media") || o.Contains("embeddings"));
    }

    [Fact]
    public void Zip64_entry_count_is_refused()
    {
        byte[] z = (byte[])Docx(P("x")).Clone();
        for (int i = z.Length - 22; i >= 0; i--)
        {
            if (z[i] == 0x50 && z[i + 1] == 0x4B && z[i + 2] == 0x05 && z[i + 3] == 0x06)
            {
                z[i + 10] = 0xFF;
                z[i + 11] = 0xFF;
                break;
            }
        }
        AssertDamaged(Run(AttachmentFormat.Word, z, []));
    }

    [Fact]
    public void Cancelled_token_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => Run(AttachmentFormat.Word, Docx(P("x")), [], cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            AttachmentText.Extract("a.docx", Docx(P("x")), cts.Token));
    }
}
