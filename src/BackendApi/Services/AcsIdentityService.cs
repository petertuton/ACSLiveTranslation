using Azure.Communication;
using Azure.Communication.Identity;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackendApi.Services;

public class AcsIdentityService
{
    private readonly CommunicationIdentityClient _client;
    private readonly ILogger<AcsIdentityService> _logger;

    public AcsIdentityService(IConfiguration configuration, ILogger<AcsIdentityService> logger)
    {
        _logger = logger;
        var endpoint = configuration["AcsEndpoint"]
            ?? throw new InvalidOperationException("AcsEndpoint is not configured.");
        _client = new CommunicationIdentityClient(new Uri(endpoint), new DefaultAzureCredential());
    }

    public async Task<(CommunicationUserIdentifier User, string Token, DateTimeOffset ExpiresOn)> CreateUserAndTokenAsync()
    {
        _logger.LogInformation("Creating new ACS user and issuing VoIP token.");

        var response = await _client.CreateUserAndTokenAsync(
            scopes: new[] { CommunicationTokenScope.VoIP });

        var userAndToken = response.Value;
        return (userAndToken.User, userAndToken.AccessToken.Token, userAndToken.AccessToken.ExpiresOn);
    }

    public async Task<(string Token, DateTimeOffset ExpiresOn)> GetTokenAsync(string userId)
    {
        _logger.LogInformation("Issuing VoIP token for user {UserId}.", userId);

        var user = new CommunicationUserIdentifier(userId);
        var response = await _client.GetTokenAsync(
            user,
            scopes: new[] { CommunicationTokenScope.VoIP });

        return (response.Value.Token, response.Value.ExpiresOn);
    }

    public async Task RevokeTokensAsync(string userId)
    {
        _logger.LogInformation("Revoking tokens for user {UserId}.", userId);

        var user = new CommunicationUserIdentifier(userId);
        await _client.RevokeTokensAsync(user);
    }
}
