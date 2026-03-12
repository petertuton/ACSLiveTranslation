using System.Net;
using Azure.Identity;
using Azure.Messaging.WebPubSub;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackendApi.Functions;

public class NegotiateFunction
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<NegotiateFunction> _logger;

    public NegotiateFunction(IConfiguration configuration, ILogger<NegotiateFunction> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [Function("Negotiate")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pubsub/negotiate")] HttpRequestData req)
    {
        _logger.LogInformation("WebPubSub negotiate request received.");

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var language = query["language"];

            if (string.IsNullOrEmpty(language))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { error = "Query parameter 'language' is required." });
                return badResponse;
            }

            var endpoint = _configuration["WebPubSubEndpoint"];
            var hubName = _configuration["WebPubSubHubName"] ?? "captions";

            if (string.IsNullOrEmpty(endpoint))
            {
                _logger.LogError("WebPubSubEndpoint is not configured.");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "WebPubSub is not configured." });
                return errorResponse;
            }

            var serviceClient = new WebPubSubServiceClient(new Uri(endpoint), hubName, new DefaultAzureCredential());
            var groupName = $"captions-{language}";

            _logger.LogInformation("Generating client access URI for hub '{Hub}', group '{Group}'", hubName, groupName);

            var uri = await serviceClient.GetClientAccessUriAsync(
                expiresAfter: TimeSpan.FromHours(1),
                userId: Guid.NewGuid().ToString("N"),
                roles: new[]
                {
                    $"webpubsub.joinLeaveGroup.{groupName}",
                    $"webpubsub.sendToGroup.{groupName}"
                },
                groups: new[] { groupName });

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { url = uri.AbsoluteUri });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to negotiate WebPubSub connection.");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { error = "Failed to negotiate connection." });
            return response;
        }
    }
}
