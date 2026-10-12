using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DocumentTextFunction;

public class ReadChangeRequestFunction
{
    private readonly ILogger<ReadChangeRequestFunction> _logger;

    public ReadChangeRequestFunction(ILogger<ReadChangeRequestFunction> logger) => _logger = logger;

    [Function("ReadChangeRequest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request)
    {
        try
        {
            var (fileName, bytes) = await DocumentRequest.ReadAsync(request);
            if (bytes.Length > ChangeVision.MaxBytes)
                return await DocumentRequest.Json(request, HttpStatusCode.RequestEntityTooLarge, new { error = "File is larger than 15 MB." });

            _logger.LogInformation("Reading change marks in {FileName}, {ByteCount} bytes", fileName, bytes.Length);
            var changes = await ChangeVision.ReadAsync(fileName, bytes, request.FunctionContext.CancellationToken);
            changes["fileName"] = fileName;
            return await DocumentRequest.Json(request, HttpStatusCode.OK, changes);
        }
        catch (DocumentTextException ex)
        {
            return await DocumentRequest.Json(request, HttpStatusCode.UnprocessableEntity, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change vision failed");
            return await DocumentRequest.Json(request, HttpStatusCode.BadRequest, new { error = "The change request could not be read." });
        }
    }
}
