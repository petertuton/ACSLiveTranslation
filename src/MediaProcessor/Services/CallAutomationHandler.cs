using Azure.Communication.CallAutomation;
using Azure.Identity;

namespace MediaProcessor.Services;

/// <summary>
/// Manages ACS Call Automation interactions, including answering incoming calls
/// with media streaming and starting/stopping audio streaming from active calls.
/// </summary>
public class CallAutomationHandler
{
    private readonly CallAutomationClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CallAutomationHandler> _logger;

    public CallAutomationHandler(IConfiguration configuration, ILogger<CallAutomationHandler> logger)
    {
        var endpoint = configuration["AcsEndpoint"]
            ?? throw new InvalidOperationException("AcsEndpoint is not configured.");

        _client = new CallAutomationClient(new Uri(endpoint), new DefaultAzureCredential());
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Joins an existing group call using Call Automation's ConnectCallAsync,
    /// configuring media streaming so that audio is forwarded to the WebSocket
    /// endpoint for real-time translation.
    /// </summary>
    /// <param name="groupId">The group call ID (GUID) to join.</param>
    /// <returns>The call connection ID.</returns>
    public async Task<string> JoinGroupCallAsync(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var callbackBaseUrl = _configuration["CallbackBaseUrl"]
            ?? throw new InvalidOperationException("CallbackBaseUrl is not configured.");

        // Build the WebSocket URL for media streaming
        var wsBaseUrl = callbackBaseUrl.Replace("https://", "wss://").Replace("http://", "ws://");
        var mediaStreamingUrl = wsBaseUrl.TrimEnd('/').Replace("/api/callbacks", "") + "/api/mediastream";

        var mediaStreamingOptions = new MediaStreamingOptions(
            MediaStreamingAudioChannel.Unmixed,
            StreamingTransport.Websocket)
        {
            TransportUri = new Uri(mediaStreamingUrl),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true
        };

        var callLocator = new GroupCallLocator(groupId);
        var options = new ConnectCallOptions(callLocator, new Uri(callbackBaseUrl))
        {
            MediaStreamingOptions = mediaStreamingOptions,
            CallIntelligenceOptions = BuildCallIntelligenceOptions()
        };

        _logger.LogInformation(
            "Connecting to group call {GroupId} with media streaming to {MediaStreamUrl}",
            groupId, mediaStreamingUrl);

        var result = await _client.ConnectCallAsync(options);
        var callConnectionId = result.Value.CallConnection.CallConnectionId;

        _logger.LogInformation(
            "Connected to group call {GroupId}. CallConnectionId: {CallConnectionId}",
            groupId, callConnectionId);

        return callConnectionId;
    }

    /// <summary>
    /// Answers an incoming call and configures media streaming so that audio
    /// is forwarded to the WebSocket endpoint for real-time translation.
    /// </summary>
    /// <param name="incomingCallContext">The incoming call context from the Event Grid event.</param>
    /// <returns>The call connection ID of the answered call.</returns>
    public async Task<string> AnswerCallAsync(string incomingCallContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incomingCallContext);

        var callbackBaseUrl = _configuration["CallbackBaseUrl"]
            ?? throw new InvalidOperationException("CallbackBaseUrl is not configured.");

        // Build the WebSocket URL for media streaming
        var wsBaseUrl = callbackBaseUrl.Replace("https://", "wss://").Replace("http://", "ws://");
        var mediaStreamingUrl = wsBaseUrl.TrimEnd('/').Replace("/api/callbacks", "") + "/api/mediastream";

        var mediaStreamingOptions = new MediaStreamingOptions(
            MediaStreamingAudioChannel.Unmixed,
            StreamingTransport.Websocket)
        {
            TransportUri = new Uri(mediaStreamingUrl),
            MediaStreamingContent = MediaStreamingContent.Audio,
            StartMediaStreaming = true
        };

        var options = new AnswerCallOptions(incomingCallContext, new Uri(callbackBaseUrl))
        {
            MediaStreamingOptions = mediaStreamingOptions,
            CallIntelligenceOptions = BuildCallIntelligenceOptions()
        };

        _logger.LogInformation("Answering incoming call with media streaming to {MediaStreamUrl}", mediaStreamingUrl);
        var result = await _client.AnswerCallAsync(options);
        var callConnectionId = result.Value.CallConnection.CallConnectionId;

        _logger.LogInformation("Call answered. CallConnectionId: {CallConnectionId}", callConnectionId);
        return callConnectionId;
    }

    /// <summary>
    /// Starts media streaming for the specified call so that audio data is
    /// forwarded to this service for real-time translation.
    /// </summary>
    /// <param name="callConnectionId">The Call Automation connection ID.</param>
    public async Task StartMediaStreamingAsync(string callConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);

        try
        {
            var callConnection = _client.GetCallConnection(callConnectionId);
            var callMedia = callConnection.GetCallMedia();

            var options = new StartMediaStreamingOptions()
            {
                OperationContext = $"start-streaming-{callConnectionId}"
            };

            _logger.LogInformation("Starting media streaming for call {CallConnectionId}", callConnectionId);
            await callMedia.StartMediaStreamingAsync(options);
            _logger.LogInformation("Media streaming started for call {CallConnectionId}", callConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start media streaming for call {CallConnectionId}", callConnectionId);
            throw;
        }
    }

    /// <summary>
    /// Stops media streaming for the specified call.
    /// </summary>
    /// <param name="callConnectionId">The Call Automation connection ID.</param>
    public async Task StopMediaStreamingAsync(string callConnectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);

        try
        {
            var callConnection = _client.GetCallConnection(callConnectionId);
            var callMedia = callConnection.GetCallMedia();

            var options = new StopMediaStreamingOptions()
            {
                OperationContext = $"stop-streaming-{callConnectionId}"
            };

            _logger.LogInformation("Stopping media streaming for call {CallConnectionId}", callConnectionId);
            await callMedia.StopMediaStreamingAsync(options);
            _logger.LogInformation("Media streaming stopped for call {CallConnectionId}", callConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop media streaming for call {CallConnectionId}", callConnectionId);
            throw;
        }
    }

    /// <summary>
    /// Maps language codes to Azure Neural TTS voice names.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultVoices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"]      = "en-US-JennyNeural",
        ["en-US"]   = "en-US-JennyNeural",
        ["es"]      = "es-ES-ElviraNeural",
        ["fr"]      = "fr-FR-DeniseNeural",
        ["de"]      = "de-DE-KatjaNeural",
        ["zh-Hans"] = "zh-CN-XiaoxiaoNeural",
        ["ja"]      = "ja-JP-NanamiNeural",
        ["ko"]      = "ko-KR-SunHiNeural",
        ["pt"]      = "pt-BR-FranciscaNeural",
        ["ar"]      = "ar-SA-ZariyahNeural"
    };

    private CallIntelligenceOptions? BuildCallIntelligenceOptions()
    {
        var endpoint = _configuration["Speech:Endpoint"];
        if (string.IsNullOrEmpty(endpoint))
            return null;

        return new CallIntelligenceOptions { CognitiveServicesEndpoint = new Uri(endpoint) };
    }

    /// <summary>
    /// Plays translated text as synthesized speech into the call using Call Automation's built-in TTS.
    /// </summary>
    public async Task PlayTranslatedAudioAsync(string callConnectionId, string text, string language)
    {
        try
        {
            var callConnection = _client.GetCallConnection(callConnectionId);
            var callMedia = callConnection.GetCallMedia();

            var voiceName = DefaultVoices.GetValueOrDefault(language, "en-US-JennyNeural");

            var textSource = new TextSource(text) { VoiceName = voiceName };
            var playOptions = new PlayToAllOptions(textSource)
            {
                OperationContext = $"tts-{language}"
            };

            _logger.LogInformation("Playing TTS ({Language}, {Voice}) on call {CallConnectionId}: \"{Text}\"",
                language, voiceName, callConnectionId, text[..Math.Min(text.Length, 80)]);

            await callMedia.PlayToAllAsync(playOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play TTS audio for call {CallConnectionId}, language {Language}",
                callConnectionId, language);
        }
    }
}
