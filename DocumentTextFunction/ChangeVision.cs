using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocumentTextFunction;

public static class ChangeVision
{
    public const int MaxBytes = 15 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    private const string Instruction =
        """
        You are looking at a change-request page. Read the printed text and every visible comment bubble, callout, or arrow.
        For each bubble, name the field label and the value the arrow points at. The arrow tip is the target, not the text nearest the bubble box.
        Return JSON only, with this shape:
        {"changes":[{"page":1,"field":"Country","pointedAt":"UK","bubble":"Should be United Kingdom, not UK","requestedValue":"United Kingdom"}]}
        Use null for requestedValue when the bubble does not state a replacement. Use null for field and pointedAt when you cannot see the arrow target. Still include that bubble.
        Do not include ordinary document text that is not a requested change.
        """;

    public static async Task<JsonObject> ReadAsync(string fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        var content = new JsonArray();
        AppendDocument(content, "Change request", fileName, bytes, visionOnly: true);
        content.Add(new JsonObject { ["type"] = "text", ["text"] = Instruction });
        return await CompleteAsync(content, cancellationToken);
    }

    public static void AppendDocument(JsonArray content, string heading, string fileName, byte[] bytes, bool visionOnly)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        JsonObject? visual = extension switch
        {
            ".pdf" => FilePart(fileName, "application/pdf", bytes),
            ".png" => ImagePart("image/png", bytes),
            ".jpg" or ".jpeg" => ImagePart("image/jpeg", bytes),
            ".webp" => ImagePart("image/webp", bytes),
            ".gif" => ImagePart("image/gif", bytes),
            ".bmp" => ImagePart("image/bmp", bytes),
            _ => null
        };

        if (visual is not null)
        {
            var pages = extension == ".pdf" ? PdfPageCount(bytes) : 0;
            var label = pages > 0
                ? $"{heading} ({fileName}), {pages} page{(pages == 1 ? "" : "s")}. Check every page."
                : $"{heading} ({fileName})";
            content.Add(new JsonObject { ["type"] = "text", ["text"] = label });
            content.Add(visual);
            if (extension == ".pdf")
                AppendPdfText(content, heading, fileName, bytes);
            return;
        }

        if (visionOnly)
            throw new DocumentTextException("The vision read needs a PDF or an image so the arrow is visible.");

        var extracted = DocumentTextExtractor.Extract(fileName, bytes);
        var text = string.IsNullOrWhiteSpace(extracted.Text) ? "(no text found)" : extracted.Text;
        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"{heading} ({fileName})\n{text}"
        });
    }

    private const int MaxPdfTextCharacters = 60_000;

    private static void AppendPdfText(JsonArray content, string heading, string fileName, byte[] bytes)
    {
        string text;
        try
        {
            text = DocumentTextExtractor.Extract(fileName, bytes).Text;
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;
        if (text.Length > MaxPdfTextCharacters)
            text = text[..MaxPdfTextCharacters];

        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"Text and comment bubbles read from the {heading} ({fileName}):\n{text}"
        });
    }

    private static int PdfPageCount(byte[] bytes)
    {
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(
                bytes,
                new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true });
            return document.NumberOfPages;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static async Task<JsonObject> CompleteAsync(JsonArray content, CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")?.Trim().TrimEnd('/');
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var apiVersion = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION");
        if (string.IsNullOrWhiteSpace(apiVersion))
            apiVersion = "2025-01-01-preview";

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(deployment))
            throw new DocumentTextException("Vision is not configured on the function app.");

        var body = new JsonObject
        {
            ["temperature"] = 0,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            }
        };

        var url = $"{endpoint}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(apiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("api-key", key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new DocumentTextException(ShortError(payload, (int)response.StatusCode));

        return ParseModelContent(payload);
    }

    public static JsonObject ParseModelContent(string payload)
    {
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new DocumentTextException("The vision model returned an empty response.");
        var content = root["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content))
            throw new DocumentTextException("The vision model returned no changes.");

        content = content.Trim();
        if (content.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = content.IndexOf('\n');
            var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                content = content[(firstLine + 1)..lastFence].Trim();
        }

        if (JsonNode.Parse(content) is not JsonObject changes)
            throw new DocumentTextException("The vision model did not return JSON.");

        changes["changes"] ??= new JsonArray();
        changes["model"] = root["model"] is JsonValue model && model.TryGetValue<string>(out var modelName)
            ? modelName
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "";
        changes["promptTokens"] = Tokens(root, "prompt_tokens");
        changes["completionTokens"] = Tokens(root, "completion_tokens");
        changes["totalTokens"] = Tokens(root, "total_tokens");
        changes["estimatedCost"] = EstimatedCost(Tokens(root, "prompt_tokens"), Tokens(root, "completion_tokens"));
        changes["costCurrency"] = Setting("MODEL_COST_CURRENCY", "USD");
        return changes;
    }

    public static decimal EstimatedCost(int promptTokens, int completionTokens)
    {
        var inputRate = Rate("MODEL_INPUT_COST_PER_MILLION", 2.75m);
        var outputRate = Rate("MODEL_OUTPUT_COST_PER_MILLION", 11.00m);
        var cost = promptTokens / 1_000_000m * inputRate + completionTokens / 1_000_000m * outputRate;
        return Math.Round(cost, 4, MidpointRounding.AwayFromZero);
    }

    private static decimal Rate(string name, decimal fallback) =>
        decimal.TryParse(
            Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var rate) && rate >= 0
            ? rate
            : fallback;

    private static string Setting(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int Tokens(JsonObject root, string name) =>
        root["usage"]?[name] is JsonValue value && value.TryGetValue<int>(out var count) ? count : 0;

    private static JsonObject FilePart(string fileName, string mediaType, byte[] bytes) => new()
    {
        ["type"] = "file",
        ["file"] = new JsonObject
        {
            ["filename"] = fileName,
            ["file_data"] = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"
        }
    };

    private static JsonObject ImagePart(string mediaType, byte[] bytes) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject
        {
            ["url"] = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"
        }
    };

    private static string ShortError(string payload, int statusCode)
    {
        try
        {
            var message = JsonNode.Parse(payload)?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
                return message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException)
        {
        }

        return $"The vision model returned HTTP {statusCode}.";
    }
}
