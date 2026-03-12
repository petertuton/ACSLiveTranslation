using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;

namespace MediaProcessor.Services;

/// <summary>
/// Synthesises translated text into audio using Azure AI Speech TTS.
/// Caches auth tokens and reuses SpeechSynthesizer instances per language
/// to eliminate per-call setup overhead (~500-1000 ms saved per call).
/// </summary>
public class TextToSpeechService : IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TextToSpeechService> _logger;
    private readonly DefaultAzureCredential _credential = new();
    private readonly ConcurrentDictionary<string, SpeechSynthesizer> _synthesizers = new();
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private AccessToken _cachedToken;

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

    public TextToSpeechService(IConfiguration configuration, ILogger<TextToSpeechService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Synthesises the supplied text into audio using SSML with slightly faster
    /// prosody (rate +15 %) so translated speech keeps pace with the speaker.
    /// Returns a compact MP3 suitable for browser playback.
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string text, string language)
    {
        var synthesizer = await GetOrCreateSynthesizerAsync(language);

        var voiceName = DefaultVoices.GetValueOrDefault(language, "en-US-JennyNeural");

        // SSML: speed up speech slightly so translated audio doesn't fall behind
        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{language}">
              <voice name="{voiceName}">
                <prosody rate="+15%">{System.Security.SecurityElement.Escape(text)}</prosody>
              </voice>
            </speak>
            """;

        _logger.LogInformation("Synthesising TTS for language {Language}: \"{Text}\"", language, text);

        using var result = await synthesizer.SpeakSsmlAsync(ssml);

        if (result.Reason == ResultReason.SynthesizingAudioCompleted)
        {
            _logger.LogInformation("TTS synthesis completed for language {Language} ({Bytes} bytes)",
                language, result.AudioData.Length);
            return result.AudioData;
        }

        if (result.Reason == ResultReason.Canceled)
        {
            var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
            _logger.LogError("TTS synthesis canceled for language {Language}: {Reason} - {ErrorDetails}",
                language, cancellation.Reason, cancellation.ErrorDetails);

            // Connection may be stale — evict so next call reconnects
            if (_synthesizers.TryRemove(language, out var stale))
                stale.Dispose();
        }

        return [];
    }

    /// <summary>
    /// Returns a cached SpeechSynthesizer for the given language, creating one if needed.
    /// The synthesizer keeps its connection open across calls, avoiding reconnect overhead.
    /// </summary>
    private async Task<SpeechSynthesizer> GetOrCreateSynthesizerAsync(string language)
    {
        if (_synthesizers.TryGetValue(language, out var existing))
            return existing;

        var region = _configuration["Speech:Region"]
            ?? throw new InvalidOperationException("Speech:Region is not configured.");
        var resourceId = _configuration["Speech:ResourceId"]
            ?? throw new InvalidOperationException("Speech:ResourceId is not configured.");

        var authToken = await GetCachedAuthTokenAsync(resourceId);

        var speechConfig = SpeechConfig.FromAuthorizationToken(authToken, region);
        speechConfig.SpeechSynthesisLanguage = language;

        if (DefaultVoices.TryGetValue(language, out var voiceName))
            speechConfig.SpeechSynthesisVoiceName = voiceName;

        // Lower bitrate MP3 for smaller payloads and faster transfer
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio16Khz32KBitRateMonoMp3);

        var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig: null);

        // Pre-open the WebSocket connection so the first SpeakSsmlAsync call
        // doesn't pay the connection-establishment penalty (~1-2 s).
        var connection = Microsoft.CognitiveServices.Speech.Connection.FromSpeechSynthesizer(synthesizer);
        connection.Open(true);

        _synthesizers[language] = synthesizer;

        _logger.LogInformation("Created reusable TTS synthesizer for language {Language} (connection pre-opened)", language);
        return synthesizer;
    }

    /// <summary>
    /// Returns a cached Entra ID auth token, refreshing it only when it's within
    /// 5 minutes of expiry.
    /// </summary>
    private async Task<string> GetCachedAuthTokenAsync(string resourceId)
    {
        if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return $"aad#{resourceId}#{_cachedToken.Token}";

        await _tokenLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
                return $"aad#{resourceId}#{_cachedToken.Token}";

            _cachedToken = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));
            _logger.LogInformation("Refreshed Entra ID token for TTS (expires {Expiry})", _cachedToken.ExpiresOn);
            return $"aad#{resourceId}#{_cachedToken.Token}";
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Pre-creates and connects TTS synthesizers for every language in the
    /// supplied list.  Call once at startup so the first real TTS request is fast.
    /// </summary>
    public async Task WarmupAsync(IEnumerable<string> languages)
    {
        foreach (var lang in languages)
        {
            try
            {
                await GetOrCreateSynthesizerAsync(lang);
                _logger.LogInformation("TTS warmup: synthesizer for {Language} ready", lang);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTS warmup: failed to pre-create synthesizer for {Language}", lang);
            }
        }
    }

    public void Dispose()
    {
        foreach (var synth in _synthesizers.Values)
            synth.Dispose();
        _synthesizers.Clear();
        _tokenLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
