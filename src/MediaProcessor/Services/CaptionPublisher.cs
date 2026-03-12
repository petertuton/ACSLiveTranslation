using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.WebPubSub;
using MediaProcessor.Models;

namespace MediaProcessor.Services;

/// <summary>
/// Publishes translated captions to Azure Web PubSub, routing each caption
/// to a group named after its target language so that clients receive only
/// the languages they subscribe to.
/// </summary>
public class CaptionPublisher
{
    private readonly WebPubSubServiceClient _serviceClient;
    private readonly ILogger<CaptionPublisher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CaptionPublisher(IConfiguration configuration, ILogger<CaptionPublisher> logger)
    {
        var endpoint = configuration["WebPubSub:Endpoint"]
            ?? throw new InvalidOperationException("WebPubSub:Endpoint is not configured.");
        var hubName = configuration["WebPubSub:HubName"] ?? "captions";

        _serviceClient = new WebPubSubServiceClient(new Uri(endpoint), hubName, new DefaultAzureCredential());
        _logger = logger;
    }

    /// <summary>
    /// Publishes a caption message to the Web PubSub group for the caption's target language.
    /// </summary>
    /// <param name="caption">The caption message to publish.</param>
    public async Task PublishCaptionAsync(CaptionMessage caption)
    {
        ArgumentNullException.ThrowIfNull(caption);

        var group = $"captions-{caption.Language}";
        var payload = new { type = "caption", data = caption };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            await _serviceClient.SendToGroupAsync(group, json, Azure.Core.ContentType.ApplicationJson);

            _logger.LogDebug("Published caption to group {Group} for session {SessionId}",
                group, caption.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish caption to group {Group} for session {SessionId}",
                group, caption.SessionId);
            throw;
        }
    }

    /// <summary>
    /// Publishes synthesized TTS audio (base64-encoded MP3) to the Web PubSub group
    /// for a target language so that clients can play it back.
    /// </summary>
    /// <param name="language">The target language code (e.g., "es").</param>
    /// <param name="audioData">MP3 audio bytes.</param>
    /// <param name="participantId">Client-generated participant ID for filtering.</param>
    public async Task PublishAudioAsync(string language, byte[] audioData, string? participantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (audioData is not { Length: > 0 })
            return;

        var group = $"captions-{language}";
        var payload = new { type = "audio", audioBase64 = Convert.ToBase64String(audioData), participantId = participantId ?? "" };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        try
        {
            await _serviceClient.SendToGroupAsync(group, json, Azure.Core.ContentType.ApplicationJson);

            _logger.LogInformation("Published TTS audio ({Bytes} bytes) to group {Group}",
                audioData.Length, group);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish TTS audio to group {Group}", group);
        }
    }
}
