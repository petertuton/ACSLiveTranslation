using System.Collections.Concurrent;
using Azure.Identity;
using MediaProcessor.Models;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;

namespace MediaProcessor.Services;

/// <summary>
/// Performs real-time speech-to-text translation using Azure AI Speech SDK.
/// Receives raw audio, recognises speech in the source language, and translates
/// it into every configured target language.
/// </summary>
public class SpeechTranslationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpeechTranslationService> _logger;
    private readonly ConcurrentDictionary<string, (TranslationRecognizer Recognizer, PushAudioInputStream PushStream)> _sessions = new();

    public SpeechTranslationService(IConfiguration configuration, ILogger<SpeechTranslationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Begins continuous speech translation on the provided audio stream.
    /// Recognition and translation results are delivered through the <paramref name="onResult"/> callback.
    /// </summary>
    /// <param name="audioStream">PCM audio stream (16 kHz, 16-bit, mono) to translate.</param>
    /// <param name="sessionId">Caller-supplied session identifier for correlation.</param>
    /// <param name="sourceLanguage">BCP-47 source language code (e.g., "fr-FR").</param>
    /// <param name="targetLanguages">Short language codes to translate into (e.g. "en", "fr").</param>
    /// <param name="onResult">Async callback invoked for every partial and final translation result.</param>
    /// <param name="ct">Cancellation token to stop translation.</param>
    public async Task StartTranslationAsync(
        Stream audioStream,
        string sessionId,
        string sourceLanguage,
        string[] targetLanguages,
        Func<TranslationResult, Task> onResult,
        CancellationToken ct)
    {
        var region = _configuration["Speech:Region"]
            ?? throw new InvalidOperationException("Speech:Region is not configured.");
        var resourceId = _configuration["Speech:ResourceId"]
            ?? throw new InvalidOperationException("Speech:ResourceId is not configured. Set it to the full Azure resource ID of your Speech resource.");

        // Use Entra ID (DefaultAzureCredential) for token-based auth
        // Speech SDK requires format: aad#<resourceId>#<aadToken>
        var credential = new DefaultAzureCredential();
        var tokenResult = await credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
        var authToken = $"aad#{resourceId}#{tokenResult.Token}";
        _logger.LogInformation("[{SessionId}] Acquired Entra ID token for Speech service (expires {Expiry})",
            sessionId, tokenResult.ExpiresOn);

        var translationConfig = SpeechTranslationConfig.FromAuthorizationToken(authToken, region);
        // translationConfig.SetProperty(PropertyId.Speech_LogFilename, $"speech-sdk-{sessionId[..8]}.log");

        foreach (var lang in targetLanguages)
        {
            translationConfig.AddTargetLanguage(lang);
        }

        // 16 kHz, 16-bit, mono PCM
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        var pushStream = AudioInputStream.CreatePushStream(audioFormat);
        var audioConfig = AudioConfig.FromStreamInput(pushStream);

        // Use the explicit source language provided by the caller
        translationConfig.SpeechRecognitionLanguage = sourceLanguage;
        var recognizer = new TranslationRecognizer(translationConfig, audioConfig);
        _logger.LogInformation("[{SessionId}] Using fixed source language: {Language}",
            sessionId, sourceLanguage);

        _sessions[sessionId] = (recognizer, pushStream);

        recognizer.Recognizing += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Result.Text))
                return;

            _logger.LogInformation("[{SessionId}] Recognizing: {Text}", sessionId, e.Result.Text);
            var detectedLang = GetDetectedLanguage(e.Result) ?? sourceLanguage;
            var result = BuildTranslationResult(e.Result, detectedLang, isFinal: false);
            _ = SafeInvokeCallback(onResult, result, sessionId);
        };

        recognizer.Recognized += (s, e) =>
        {
            if (e.Result.Reason != ResultReason.TranslatedSpeech || string.IsNullOrWhiteSpace(e.Result.Text))
                return;

            _logger.LogInformation("[{SessionId}] Recognized: {Text}", sessionId, e.Result.Text);
            var detectedLang = GetDetectedLanguage(e.Result) ?? sourceLanguage;
            _logger.LogInformation("[{SessionId}] Detected language: {Language}", sessionId, detectedLang);
            var result = BuildTranslationResult(e.Result, detectedLang, isFinal: true);
            _ = SafeInvokeCallback(onResult, result, sessionId);
        };

        recognizer.Canceled += (s, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                _logger.LogError("Translation canceled for session {SessionId}: {ErrorCode} - {ErrorDetails}",
                    sessionId, e.ErrorCode, e.ErrorDetails);
            }
            else
            {
                _logger.LogInformation("Translation ended for session {SessionId}: {Reason}", sessionId, e.Reason);
            }
        };

        recognizer.SessionStarted += (s, e) =>
        {
            _logger.LogInformation("[{SessionId}] Speech SDK session STARTED (connected to service)", sessionId);
        };

        recognizer.SessionStopped += (s, e) =>
        {
            _logger.LogInformation("[{SessionId}] Speech SDK session STOPPED", sessionId);
        };

        recognizer.SpeechStartDetected += (s, e) =>
        {
            _logger.LogDebug("[{SessionId}] Speech START detected at offset {Offset}", sessionId, e.Offset);
        };

        recognizer.SpeechEndDetected += (s, e) =>
        {
            _logger.LogDebug("[{SessionId}] Speech END detected at offset {Offset}", sessionId, e.Offset);
        };

        _logger.LogInformation("Starting speech translation for session {SessionId} ({Source} → {Targets})",
            sessionId, sourceLanguage, string.Join(", ", targetLanguages));

        await recognizer.StartContinuousRecognitionAsync();

        // Pump audio data into the push stream
        _ = Task.Run(async () =>
        {
            long bytesTotal = 0;
            try
            {
                var buffer = new byte[3200]; // 100 ms of 16 kHz 16-bit mono audio
                int bytesRead;
                while (!ct.IsCancellationRequested &&
                       (bytesRead = await audioStream.ReadAsync(buffer, ct)) > 0)
                {
                    pushStream.Write(buffer, bytesRead);
                    bytesTotal += bytesRead;
                    if (bytesTotal == bytesRead) // first chunk
                    {
                        _logger.LogDebug("[{SessionId}] Audio pump: first {Bytes} bytes written to Speech SDK", sessionId, bytesRead);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Audio pump canceled for session {SessionId} (total {Bytes} bytes)", sessionId, bytesTotal);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pumping audio for session {SessionId}", sessionId);
            }
            finally
            {
                _logger.LogInformation("[{SessionId}] Audio pump finished — {Bytes} total bytes written", sessionId, bytesTotal);
                pushStream.Close();
            }
        }, ct);
    }

    /// <summary>
    /// Stops continuous translation and releases SDK resources.
    /// </summary>
    public async Task StopTranslationAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            _logger.LogInformation("Stopping speech translation recognizer for session {SessionId}", sessionId);
            await session.Recognizer.StopContinuousRecognitionAsync();
            session.Recognizer.Dispose();
            session.PushStream.Close();
        }
    }

    private static string? GetDetectedLanguage(TranslationRecognitionResult result)
    {
        var autoDetectResult = AutoDetectSourceLanguageResult.FromResult(result);
        var lang = autoDetectResult?.Language;
        return string.IsNullOrEmpty(lang) || lang == "Unknown" ? null : lang;
    }

    private static TranslationResult BuildTranslationResult(
        TranslationRecognitionResult result,
        string sourceLanguage,
        bool isFinal)
    {
        var translations = new Dictionary<string, string>();
        foreach (var kvp in result.Translations)
        {
            translations[kvp.Key] = kvp.Value;
        }

        return new TranslationResult
        {
            OriginalText = result.Text,
            SourceLanguage = sourceLanguage,
            Translations = translations,
            Timestamp = DateTimeOffset.UtcNow,
            IsFinal = isFinal
        };
    }

    private async Task SafeInvokeCallback(
        Func<TranslationResult, Task> onResult,
        TranslationResult result,
        string sessionId)
    {
        try
        {
            await onResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in translation callback for session {SessionId}", sessionId);
        }
    }
}
