using System.Net.Http.Json;

namespace KioskClient.Services;

public class MediaProcessorClient
{
    private readonly HttpClient _httpClient;

    public MediaProcessorClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;

        var baseUrl = configuration["MediaProcessor:BaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    public async Task JoinGroupCallAsync(string groupId, string sourceLanguage, string participantId, string acsUserId)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/join", new { groupId, sourceLanguage, participantId, acsUserId });
        response.EnsureSuccessStatusCode();
    }
}
