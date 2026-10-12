using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace DocumentTextFunction;

public static class DocumentRequest
{
    public const int MaxBytes = 25 * 1024 * 1024;

    public static async Task<(string FileName, byte[] Bytes)> ReadAsync(HttpRequestData request)
    {
        var contentType = request.Headers.TryGetValues("Content-Type", out var types)
            ? types.FirstOrDefault() ?? ""
            : "";

        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var payload = await JsonSerializer.DeserializeAsync<ExtractRequest>(
                request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload is null || string.IsNullOrWhiteSpace(payload.ContentBase64))
                throw new DocumentTextException("JSON body must include fileName and contentBase64.");

            return (string.IsNullOrWhiteSpace(payload.FileName) ? "document" : payload.FileName, ContentDecoder.Decode(payload.ContentBase64));
        }

        var fileName = request.Headers.TryGetValues("x-file-name", out var names)
            ? names.FirstOrDefault() ?? "document"
            : "document";

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        return (fileName, buffer.ToArray());
    }

    public static async Task<HttpResponseData> Json(HttpRequestData request, HttpStatusCode status, object body)
    {
        var response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body));
        return response;
    }

    private sealed class ExtractRequest
    {
        public string? FileName { get; set; }
        public string? ContentBase64 { get; set; }
    }
}
