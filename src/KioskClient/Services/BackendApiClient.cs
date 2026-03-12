using System.Net.Http.Json;
using System.Text.Json;
using KioskClient.Models;

namespace KioskClient.Services;

public class BackendApiClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public BackendApiClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;

        var baseUrl = configuration["BackendApi:BaseUrl"];
        if (!string.IsNullOrEmpty(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    public async Task<TokenInfo> GetTokenAsync()
    {
        var response = await _httpClient.GetAsync("/api/token");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TokenInfo>(JsonOptions);
        return result ?? throw new InvalidOperationException("Failed to deserialize token response.");
    }

    public async Task<SessionInfo> CreateSessionAsync(string[] targetLanguages)
    {
        var payload = new { targetLanguages };
        var response = await _httpClient.PostAsJsonAsync("/api/sessions", payload);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SessionInfo>(JsonOptions);
        return result ?? throw new InvalidOperationException("Failed to deserialize session response.");
    }

    public async Task<string> NegotiatePubSubAsync(string language)
    {
        var response = await _httpClient.GetAsync($"/api/pubsub/negotiate?language={Uri.EscapeDataString(language)}");
        response.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        if (doc.RootElement.TryGetProperty("url", out var urlProp))
        {
            return urlProp.GetString() ?? string.Empty;
        }

        return await response.Content.ReadAsStringAsync();
    }
}
