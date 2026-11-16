using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ClippysRevenge.Core.Tests;

/// <summary>PDFs built in the test run. Nothing binary is checked in.</summary>
internal static class PdfFixtures
{
    /// <summary>One page per string; long strings are wrapped into 100-character lines.</summary>
    public static byte[] Pages(params string[] pageTexts)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (string text in pageTexts)
        {
            var page = b.AddPage(PageSize.A4);
            double y = 800;
            for (int i = 0; i < text.Length; i += 100)
            {
                page.AddText(text.Substring(i, Math.Min(100, text.Length - i)), 8, new PdfPoint(10, y), font);
                y -= 10;
            }
        }
        return b.Build();
    }

    /// <summary>Pages with a drawn rectangle only, no text.</summary>
    public static byte[] Blank(int pages)
    {
        var b = new PdfDocumentBuilder();
        for (int i = 0; i < pages; i++)
        {
            var page = b.AddPage(PageSize.A4);
            page.DrawRectangle(new PdfPoint(20, 20), 100, 100);
        }
        return b.Build();
    }

    /// <summary>A hand-written PDF 1.4 with a standard security handler whose /U does not validate for the empty password.</summary>
    public static byte[] Encrypted()
    {
        string hex32 = new string('A', 64);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Filter /Standard /V 1 /R 2 /Length 40 /P -4 /O <{hex32}> /U <{hex32}> >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Encrypt 4 0 R /ID [<{hex32[..32]}> <{hex32[..32]}>] >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
