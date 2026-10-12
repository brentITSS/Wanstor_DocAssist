using System.Text;
using DocumentTextFunction;
using Xunit;

namespace DocumentTextFunction.Tests;

public class PdfCalloutTests
{
    [Fact]
    public void Callout_arrow_is_tied_to_the_line_it_points_at()
    {
        var result = DocumentTextExtractor.Extract("change.pdf", PdfWithCallout());

        Assert.Contains("Tradex Insurance Company Limited", result.Text);
        Assert.Contains("pointing at \"Tradex Insurance Company Limited\"", result.Text);
        Assert.Contains("Change Limited to PLC", result.Text);
    }

    [Fact]
    public void Rtf_control_words_are_not_returned_as_text()
    {
        var rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0\\fs24 Change the company name.\\par }";
        var result = DocumentTextExtractor.Extract("change.rtf", Encoding.ASCII.GetBytes(rtf));

        Assert.Contains("Change the company name.", result.Text);
        Assert.DoesNotContain("fonttbl", result.Text);
    }

    [Fact]
    public void Rtf_text_box_is_included()
    {
        var rtf = "{\\rtf1\\ansi Country UK\\par{\\shp{\\*\\shpinst{\\sp{\\sn shapeType}{\\sv 202}}{\\shptxt Should be United Kingdom, not UK\\par}}}\\par}";
        var result = DocumentTextExtractor.Extract("change.rtf", Encoding.ASCII.GetBytes(rtf));

        Assert.Contains("Country UK", result.Text);
        Assert.Contains("[Text box] Should be United Kingdom, not UK", result.Text);
        Assert.DoesNotContain("shapeType", result.Text);
    }

    [Fact]
    public void Rtf_shape_fallback_is_not_repeated()
    {
        var rtf = "{\\rtf1\\ansi{\\shp{\\*\\shpinst{\\shptxt Should be United Kingdom, not UK}}{\\shprslt Should be United Kingdom, not UK}}}";
        var result = DocumentTextExtractor.Extract("change.rtf", Encoding.ASCII.GetBytes(rtf));
        var text = result.Text;

        var first = text.IndexOf("Should be United Kingdom, not UK", StringComparison.Ordinal);
        var second = text.IndexOf("Should be United Kingdom, not UK", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, second);
    }

    internal static byte[] PdfWithCallout()
    {
        var stream = "BT /F1 12 Tf 72 100 Td (Tradex Insurance Company Limited) Tj ET\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> /Annots [5 0 R] >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Type /Annot /Subtype /FreeText /IT /FreeTextCallout /Contents (Change Limited to PLC) /T (Reviewer) /Rect [300 180 480 230] /CL [90 106 200 160 320 200] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var obj in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(offsets.Count).Append(" 0 obj\n").Append(obj).Append("\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n");
        pdf.Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
