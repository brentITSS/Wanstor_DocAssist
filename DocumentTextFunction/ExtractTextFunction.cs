using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DocumentTextFunction;

public class ExtractTextFunction
{
    private readonly ILogger<ExtractTextFunction> _logger;

    public ExtractTextFunction(ILogger<ExtractTextFunction> logger) => _logger = logger;

    [Function("ExtractText")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request)
    {
        try
        {
            var (fileName, bytes) = await DocumentRequest.ReadAsync(request);
            if (bytes.Length > DocumentRequest.MaxBytes)
                return await DocumentRequest.Json(request, HttpStatusCode.RequestEntityTooLarge, new { error = "File is larger than 25 MB." });

            _logger.LogInformation("Extracting text from {FileName}, {ByteCount} bytes", fileName, bytes.Length);
            var result = DocumentTextExtractor.Extract(fileName, bytes);
            return await DocumentRequest.Json(request, HttpStatusCode.OK, new
            {
                fileName,
                text = result.Text,
                warning = result.Warning
            });
        }
        catch (DocumentTextException ex)
        {
            return await DocumentRequest.Json(request, HttpStatusCode.UnprocessableEntity, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text extraction failed");
            return await DocumentRequest.Json(request, HttpStatusCode.BadRequest, new { error = "The file could not be read." });
        }
    }
}
