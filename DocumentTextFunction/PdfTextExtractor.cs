using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Tokens;

namespace DocumentTextFunction;

internal static class PdfTextExtractor
{
    private const double MaxArrowDistance = 48;

    public static ExtractionResult Extract(byte[] bytes)
    {
        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
            var text = new StringBuilder();
            var sawPageText = false;
            var sawBubble = false;

            foreach (var page in document.GetPages())
            {
                var pageText = ReadPage(page);
                var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    sawPageText = true;
                    text.AppendLine(pageText.Trim());
                    text.AppendLine();
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var annotation in SafeAnnotations(page))
                {
                    if (annotation.Type == AnnotationType.Link)
                        continue;
                    if (annotation.Flags.HasFlag(AnnotationFlags.Hidden) || annotation.Flags.HasFlag(AnnotationFlags.Invisible))
                        continue;

                    var bubble = BubbleText(annotation);
                    if (string.IsNullOrWhiteSpace(bubble))
                        continue;

                    var key = bubble.Trim();
                    if (!seen.Add(key))
                        continue;

                    sawBubble = true;
                    var target = ArrowTarget(annotation, words);
                    text.Append("[Comment on page ").Append(page.Number);
                    var author = Author(annotation);
                    if (!string.IsNullOrWhiteSpace(author))
                        text.Append(" by ").Append(author);
                    if (!string.IsNullOrWhiteSpace(target))
                        text.Append(", pointing at \"").Append(target).Append('"');
                    else if (HasArrow(annotation))
                        text.Append(", arrow target not in the text layer");
                    text.Append("] ").AppendLine(key);
                    text.AppendLine();
                }
            }

            var warning = !sawPageText && sawBubble
                ? "The PDF has no text layer. Comment bubbles were returned. Scanned page text needs OCR."
                : !sawPageText
                    ? "The PDF has no text layer. Scanned page text needs OCR."
                    : null;

            return new ExtractionResult(text.ToString().Trim(), warning);
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new DocumentTextException("The PDF is password protected.");
        }
    }

    private static string ReadPage(Page page)
    {
        try { return ContentOrderTextExtractor.GetText(page) ?? ""; }
        catch { return page.Text ?? ""; }
    }

    private static IEnumerable<Annotation> SafeAnnotations(Page page)
    {
        try { return page.GetAnnotations().ToList(); }
        catch { return Array.Empty<Annotation>(); }
    }

    private static string BubbleText(Annotation annotation)
    {
        if (!string.IsNullOrWhiteSpace(annotation.Content))
            return annotation.Content.Trim();

        if (annotation.AnnotationDictionary.TryGet(NameToken.Create("RC"), out StringToken rich))
            return StripTags(rich.Data);

        return "";
    }

    private static string? Author(Annotation annotation) =>
        annotation.AnnotationDictionary.TryGet(NameToken.Create("T"), out StringToken title) ? title.Data : null;

    private static bool HasArrow(Annotation annotation) =>
        TryArrowTip(annotation, out _, out _) || TryQuadBounds(annotation, out _);

    private static string ArrowTarget(Annotation annotation, IReadOnlyList<Word> words)
    {
        if (TryQuadBounds(annotation, out var quads))
        {
            var marked = words
                .Where(word => quads.Any(quad => Overlaps(Bounds(word), quad)))
                .OrderBy(word => word.BoundingBox.BottomLeft.Y)
                .ThenBy(word => word.BoundingBox.BottomLeft.X)
                .Select(word => word.Text);
            var markedText = string.Join(" ", marked).Trim();
            if (markedText.Length > 0)
                return markedText;
        }

        if (!TryArrowTip(annotation, out var x, out var y))
            return "";

        Word? nearest = null;
        var best = double.MaxValue;
        foreach (var word in words)
        {
            var distance = Distance(Bounds(word), x, y);
            if (distance < best)
            {
                best = distance;
                nearest = word;
            }
        }

        if (nearest is null || best > MaxArrowDistance)
            return "";

        return LineAround(words, nearest);
    }

    private static string LineAround(IReadOnlyList<Word> words, Word nearest)
    {
        var line = words
            .Where(word => OverlapsY(Bounds(word), Bounds(nearest)))
            .OrderBy(word => word.BoundingBox.BottomLeft.X)
            .ToList();

        var index = line.FindIndex(word => ReferenceEquals(word, nearest) || SameBox(word, nearest));
        if (index < 0)
            return nearest.Text;

        var start = index;
        while (start > 0 && Gap(line[start - 1], line[start]) <= 20)
            start--;
        var end = index;
        while (end < line.Count - 1 && Gap(line[end], line[end + 1]) <= 20)
            end++;

        return string.Join(
            " ",
            line.Skip(start).Take(end - start + 1).Select(word => word.Text.Trim()).Where(part => part.Length > 0));
    }

    private static bool TryArrowTip(Annotation annotation, out double x, out double y)
    {
        if (TryNumbers(annotation, "CL", out var callout) && callout.Count >= 4)
        {
            // Acrobat stores the arrow tip first and the bubble end last. Either way,
            // the tip is the endpoint farther from the comment rectangle.
            var startX = callout[0];
            var startY = callout[1];
            var endX = callout[^2];
            var endY = callout[^1];
            var box = new Box(
                annotation.Rectangle.BottomLeft.X,
                annotation.Rectangle.BottomLeft.Y,
                annotation.Rectangle.TopRight.X,
                annotation.Rectangle.TopRight.Y);
            if (Distance(box, startX, startY) >= Distance(box, endX, endY))
            {
                x = startX;
                y = startY;
            }
            else
            {
                x = endX;
                y = endY;
            }
            return true;
        }

        if (TryNumbers(annotation, "L", out var line) && line.Count >= 4)
        {
            var arrowAtEnd = true;
            if (annotation.AnnotationDictionary.TryGet(NameToken.Create("LE"), out ArrayToken endings) && endings.Data.Count >= 2)
            {
                var startName = (endings.Data[0] as NameToken)?.Data ?? "";
                var endName = (endings.Data[1] as NameToken)?.Data ?? "";
                arrowAtEnd = IsArrow(endName) || !IsArrow(startName);
            }

            x = arrowAtEnd ? line[2] : line[0];
            y = arrowAtEnd ? line[3] : line[1];
            return true;
        }

        x = y = 0;
        return false;
    }

    private static bool TryQuadBounds(Annotation annotation, out IReadOnlyList<Box> quads)
    {
        if (!TryNumbers(annotation, "QuadPoints", out var numbers) || numbers.Count < 8)
        {
            quads = Array.Empty<Box>();
            return false;
        }

        var boxes = new List<Box>();
        for (var i = 0; i + 7 < numbers.Count; i += 8)
        {
            var xs = new[] { numbers[i], numbers[i + 2], numbers[i + 4], numbers[i + 6] };
            var ys = new[] { numbers[i + 1], numbers[i + 3], numbers[i + 5], numbers[i + 7] };
            boxes.Add(new Box(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
        }

        quads = boxes;
        return boxes.Count > 0;
    }

    private static bool TryNumbers(Annotation annotation, string name, out List<double> numbers)
    {
        numbers = new List<double>();
        if (!annotation.AnnotationDictionary.TryGet(NameToken.Create(name), out ArrayToken array))
            return false;

        foreach (var token in array.Data)
        {
            if (token is not NumericToken number)
                return false;
            numbers.Add(number.Data);
        }

        return numbers.Count > 0;
    }

    private static bool IsArrow(string ending) =>
        ending.Contains("Arrow", StringComparison.OrdinalIgnoreCase);

    private static Box Bounds(Word word)
    {
        var box = word.BoundingBox;
        return new Box(box.BottomLeft.X, box.BottomLeft.Y, box.TopRight.X, box.TopRight.Y);
    }

    private static bool Overlaps(Box a, Box b) =>
        a.Right >= b.Left && a.Left <= b.Right && a.Top >= b.Bottom && a.Bottom <= b.Top;

    private static bool OverlapsY(Box a, Box b) =>
        Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom) > Math.Min(a.Top - a.Bottom, b.Top - b.Bottom) * 0.4;

    private static double Gap(Word left, Word right) =>
        Bounds(right).Left - Bounds(left).Right;

    private static bool SameBox(Word a, Word b)
    {
        var left = Bounds(a);
        var right = Bounds(b);
        return Math.Abs(left.Left - right.Left) < 0.1 && Math.Abs(left.Bottom - right.Bottom) < 0.1;
    }

    private static double Distance(Box box, double x, double y)
    {
        var dx = x < box.Left ? box.Left - x : x > box.Right ? x - box.Right : 0;
        var dy = y < box.Bottom ? box.Bottom - y : y > box.Top ? y - box.Top : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string StripTags(string value)
    {
        var text = new StringBuilder();
        var inside = false;
        foreach (var character in value)
        {
            if (character == '<') inside = true;
            else if (character == '>') inside = false;
            else if (!inside) text.Append(character);
        }
        return text.ToString().Trim();
    }

    private readonly record struct Box(double Left, double Bottom, double Right, double Top);
}
