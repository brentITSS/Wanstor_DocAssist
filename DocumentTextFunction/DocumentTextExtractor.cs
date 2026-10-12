using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DocumentTextFunction;

public static class DocumentTextExtractor
{
    public static ExtractionResult Extract(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => PdfTextExtractor.Extract(bytes),
            ".docx" or ".docm" => new ExtractionResult(OpenXmlTextExtractor.ExtractWord(bytes), null),
            ".rtf" => new ExtractionResult(RtfTextExtractor.Extract(bytes), null),
            ".txt" or ".csv" or ".md" or ".html" or ".htm" or ".xml" or ".json" =>
                new ExtractionResult(Encoding.UTF8.GetString(bytes).Trim(), null),
            ".pptx" or ".xlsx" => new ExtractionResult(OpenXmlTextExtractor.ExtractOfficeText(bytes), null),
            ".doc" => throw new DocumentTextException("Legacy .doc files are not supported. Save the file as .docx."),
            _ => throw new DocumentTextException($"'{extension}' is not supported. Use pdf, docx, rtf, pptx, xlsx, or plain text.")
        };
    }
}

public sealed record ExtractionResult(string Text, string? Warning);

public sealed class DocumentTextException : Exception
{
    public DocumentTextException(string message) : base(message) { }
}

internal static class OpenXmlTextExtractor
{
    public static string ExtractWord(byte[] bytes)
    {
        using var zip = Open(bytes);
        var text = new StringBuilder();
        AppendParts(text, zip, "word/header", "[Header]");
        AppendParts(text, zip, "word/document.xml", "[Body]");
        AppendParts(text, zip, "word/footer", "[Footer]");
        AppendParts(text, zip, "word/footnotes.xml", "[Footnote]");
        AppendParts(text, zip, "word/endnotes.xml", "[Endnote]");
        AppendComments(text, zip);
        return text.ToString().Trim();
    }

    public static string ExtractOfficeText(byte[] bytes)
    {
        using var zip = Open(bytes);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries.OrderBy(entry => entry.FullName))
        {
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.FullName.Contains("/slide", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains("sharedStrings", StringComparison.OrdinalIgnoreCase))
                continue;

            var paragraphs = Paragraphs(Load(entry));
            if (string.IsNullOrWhiteSpace(paragraphs))
                continue;
            text.AppendLine(paragraphs.Trim());
            text.AppendLine();
        }
        return text.ToString().Trim();
    }

    private static ZipArchive Open(byte[] bytes)
    {
        try { return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false); }
        catch (InvalidDataException) { throw new DocumentTextException("The Office file is not a valid package."); }
    }

    private static void AppendParts(StringBuilder text, ZipArchive zip, string namePart, string label)
    {
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.Contains(namePart, StringComparison.OrdinalIgnoreCase)))
        {
            var paragraphs = Paragraphs(Load(entry));
            if (string.IsNullOrWhiteSpace(paragraphs))
                continue;
            text.AppendLine(label);
            text.AppendLine(paragraphs.Trim());
            text.AppendLine();
        }
    }

    private static void AppendComments(StringBuilder text, ZipArchive zip)
    {
        var entry = zip.GetEntry("word/comments.xml");
        if (entry is null)
            return;

        foreach (var comment in Load(entry).Descendants().Where(element => element.Name.LocalName == "comment"))
        {
            var body = Paragraphs(comment);
            if (string.IsNullOrWhiteSpace(body))
                continue;
            var author = comment.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "author")?.Value;
            var anchors = CommentAnchors(zip, comment);
            text.Append("[Word comment");
            if (!string.IsNullOrWhiteSpace(author))
                text.Append(" by ").Append(author);
            if (!string.IsNullOrWhiteSpace(anchors))
                text.Append(", pointing at \"").Append(anchors).Append('"');
            text.Append("] ").AppendLine(body.Trim());
            text.AppendLine();
        }
    }

    private static string CommentAnchors(ZipArchive zip, XElement comment)
    {
        var id = comment.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
        var document = zip.GetEntry("word/document.xml");
        if (id is null || document is null)
            return "";

        var doc = Load(document);
        var range = doc.Descendants()
            .SkipWhile(element => element.Name.LocalName != "commentRangeStart" || IdOf(element) != id)
            .Skip(1)
            .TakeWhile(element => element.Name.LocalName != "commentRangeEnd" || IdOf(element) != id)
            .Where(element => element.Name.LocalName == "t")
            .Select(element => element.Value);

        return string.Join("", range).Trim();
    }

    private static string? IdOf(XElement element) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;

    private static XDocument Load(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static string Paragraphs(XContainer container)
    {
        var text = new StringBuilder();
        var paragraphs = container.Descendants().Where(element =>
            element.Name.LocalName == "p" &&
            !element.Descendants().Any(nested => nested != element && nested.Name.LocalName == "p"));

        foreach (var paragraph in paragraphs)
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name.LocalName == "t") text.Append(node.Value);
                else if (node.Name.LocalName == "tab") text.Append('\t');
                else if (node.Name.LocalName is "br" or "cr") text.Append('\n');
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}
