using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DocumentTextFunction;

public class CheckChangesFunction
{
    private readonly ILogger<CheckChangesFunction> _logger;

    public CheckChangesFunction(ILogger<CheckChangesFunction> logger) => _logger = logger;

    [Function("CheckChanges")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request)
    {
        try
        {
            var (requestName, requestBytes, liveName, liveBytes) = await ReadPairAsync(request);
            if (requestBytes.Length > ChangeVision.MaxBytes || liveBytes.Length > ChangeVision.MaxBytes)
                return await DocumentRequest.Json(request, HttpStatusCode.RequestEntityTooLarge, new { error = "Each file must be 15 MB or smaller." });

            _logger.LogInformation("Checking {RequestName} against {LiveName}", requestName, liveName);
            var result = await ChangeCheck.CompareAsync(requestName, requestBytes, liveName, liveBytes, request.FunctionContext.CancellationToken);
            return await DocumentRequest.Json(request, HttpStatusCode.OK, result);
        }
        catch (DocumentTextException ex)
        {
            return await DocumentRequest.Json(request, HttpStatusCode.UnprocessableEntity, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change check failed");
            return await DocumentRequest.Json(request, HttpStatusCode.BadRequest, new { error = "The documents could not be checked." });
        }
    }

    private static async Task<(string RequestName, byte[] RequestBytes, string LiveName, byte[] LiveBytes)> ReadPairAsync(HttpRequestData request)
    {
        var payload = await JsonSerializer.DeserializeAsync<CheckBody>(
            request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.RequestContentBase64)
            || string.IsNullOrWhiteSpace(payload.LiveContentBase64))
            throw new DocumentTextException("JSON body must include requestContentBase64 and liveContentBase64.");

        return (
            string.IsNullOrWhiteSpace(payload.RequestFileName) ? "request" : payload.RequestFileName,
            ContentDecoder.Decode(payload.RequestContentBase64),
            string.IsNullOrWhiteSpace(payload.LiveFileName) ? "live" : payload.LiveFileName,
            ContentDecoder.Decode(payload.LiveContentBase64));
    }

    private sealed class CheckBody
    {
        public string? RequestFileName { get; set; }
        public string? RequestContentBase64 { get; set; }
        public string? LiveFileName { get; set; }
        public string? LiveContentBase64 { get; set; }
    }
}
