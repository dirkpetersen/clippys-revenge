using System.Runtime.InteropServices;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace ClippysRevenge.Core;

/// <summary>PDF text through PdfPig (managed only). No images, OCR, annotations, forms or JavaScript (SPEC 26.4).</summary>
internal static class PdfText
{
    public static ExtractedText Extract(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        byte[] array = MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> seg)
            && seg.Array is { } a && seg.Offset == 0 && seg.Count == a.Length
            ? a
            : bytes.ToArray();

        try
        {
            using PdfDocument doc = PdfDocument.Open(array,
                new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });

            int total = doc.NumberOfPages;
            int pages = Math.Min(total, AttachmentText.MaxPdfPages);
            bool capped = total > AttachmentText.MaxPdfPages;
            var sink = new TextSink();
            bool anyText = false;

            for (int i = 1; i <= pages; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (sink.Full)
                {
                    capped = true;
                    break;
                }
                string text = ContentOrderTextExtractor.GetText(doc.GetPage(i));
                if (!string.IsNullOrWhiteSpace(text)) anyText = true;
                if (i > 1) sink.Append("\n\n");
                sink.Append(text);
            }

            if (!anyText) return ExtractedText.Skip(AttachmentReasons.NoText);
            return AttachmentText.Build(sink.ToString(), capped || sink.Full);
        }
        catch (PdfDocumentEncryptedException)
        {
            return ExtractedText.Skip(AttachmentReasons.PasswordProtected);
        }
    }
}
