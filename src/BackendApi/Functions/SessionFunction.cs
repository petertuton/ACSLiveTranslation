using System.Net;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.WebPubSub;
using BackendApi.Models;
using BackendApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackendApi.Functions;

public class SessionFunction
{
    private readonly AcsIdentityService _acsService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SessionFunction> _logger;

    public SessionFunction(
        AcsIdentityService acsService,
        IConfiguration configuration,
        ILogger<SessionFunction> logger)
    {
        _acsService = acsService;
        _configuration = configuration;
        _logger = logger;
    }

    [Function("CreateSession")]
    public async Task<HttpResponseData> CreateSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions")] HttpRequestData req)
    {
        _logger.LogInformation("Create session request received.");

        try
        {
            var requestBody = await req.ReadAsStringAsync();
            var sessionRequest = JsonSerializer.Deserialize<SessionRequest>(
                requestBody ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (sessionRequest is null || string.IsNullOrEmpty(sessionRequest.CallId))
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { error = "CallId is required." });
                return badResponse;
            }

            var (user, token, _) = await _acsService.CreateUserAndTokenAsync();
            var sessionId = Guid.NewGuid().ToString("N");

            var webPubSubUrl = GenerateWebPubSubClientUrl(sessionId, sessionRequest.TargetLanguages);

            var sessionResponse = new SessionResponse
            {
                SessionId = sessionId,
                CallId = sessionRequest.CallId,
                WebPubSubUrl = webPubSubUrl,
                AccessToken = token
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(sessionResponse);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session.");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { error = "Failed to create session." });
            return response;
        }
    }

    [Function("EndSession")]
    public async Task<HttpResponseData> EndSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}")] HttpRequestData req,
        string sessionId)
    {
        _logger.LogInformation("End session request received for session {SessionId}.", sessionId);

        try
        {
            _logger.LogInformation("Session {SessionId} ended.", sessionId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { sessionId, status = "ended" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to end session {SessionId}.", sessionId);
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { error = "Failed to end session." });
            return response;
        }
    }

    private string GenerateWebPubSubClientUrl(string sessionId, string[]? targetLanguages)
    {
        var endpoint = _configuration["WebPubSubEndpoint"];
        var hubName = _configuration["WebPubSubHubName"] ?? "captions";

        if (string.IsNullOrEmpty(endpoint))
        {
            _logger.LogWarning("WebPubSubEndpoint is not configured. Returning empty URL.");
            return string.Empty;
        }

        var serviceClient = new WebPubSubServiceClient(new Uri(endpoint), hubName, new DefaultAzureCredential());

        // Build group names from target languages (e.g., "captions-en", "captions-es")
        var groups = targetLanguages?.Length > 0
            ? targetLanguages.Select(lang => $"captions-{lang}").ToArray()
            : new[] { $"session-{sessionId}" };

        // Grant join/leave and send permissions for each language group
        var roles = groups.SelectMany(g => new[]
        {
            $"webpubsub.joinLeaveGroup.{g}",
            $"webpubsub.sendToGroup.{g}"
        }).ToArray();

        var uri = serviceClient.GetClientAccessUri(
            expiresAfter: TimeSpan.FromHours(1),
            userId: sessionId,
            roles: roles,
            groups: groups);

        return uri.AbsoluteUri;
    }
}
