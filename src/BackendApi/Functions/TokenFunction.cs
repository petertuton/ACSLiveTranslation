using System.Net;
using BackendApi.Models;
using BackendApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BackendApi.Functions;

public class TokenFunction
{
    private readonly AcsIdentityService _acsService;
    private readonly ILogger<TokenFunction> _logger;

    public TokenFunction(AcsIdentityService acsService, ILogger<TokenFunction> logger)
    {
        _acsService = acsService;
        _logger = logger;
    }

    [Function("GetToken")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "token")] HttpRequestData req)
    {
        _logger.LogInformation("Token request received.");

        try
        {
            var (user, token, expiresOn) = await _acsService.CreateUserAndTokenAsync();

            var tokenResponse = new TokenResponse
            {
                Token = token,
                ExpiresOn = expiresOn,
                UserId = user.Id
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(tokenResponse);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create ACS token.");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteAsJsonAsync(new { error = "Failed to generate token." });
            return response;
        }
    }
}
