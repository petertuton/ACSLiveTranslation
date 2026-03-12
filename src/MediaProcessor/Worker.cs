using MediaProcessor.Models;
using MediaProcessor.Services;

namespace MediaProcessor;

/// <summary>
/// Background worker that listens for incoming call events and audio streams,
/// orchestrating real-time speech translation and caption publishing.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly SpeechTranslationService _speechTranslation;
    private readonly TextToSpeechService _textToSpeech;
    private readonly CaptionPublisher _captionPublisher;
    private readonly GroupParticipantRegistry _participantRegistry;
    private readonly IConfiguration _configuration;

    public Worker(
        ILogger<Worker> logger,
        SpeechTranslationService speechTranslation,
        TextToSpeechService textToSpeech,
        CaptionPublisher captionPublisher,
        GroupParticipantRegistry participantRegistry,
        IConfiguration configuration)
    {
        _logger = logger;
        _speechTranslation = speechTranslation;
        _textToSpeech = textToSpeech;
        _captionPublisher = captionPublisher;
        _participantRegistry = participantRegistry;
        _configuration = configuration;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Media Processor worker started and ready for audio sessions");

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Waiting for incoming audio sessions...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInformation("Media Processor worker is shutting down");
    }

    /// <summary>
    /// Processes a single audio session: runs speech translation on the stream and
    /// publishes translated captions to Web PubSub for every configured target language.
    /// </summary>
    /// <param name="sessionId">Unique session identifier for this audio stream.</param>
    /// <param name="groupId">Group call ID, used to look up active participant languages.</param>
    /// <param name="sourceLanguage">BCP-47 source language code (e.g., "fr-FR"), or null to use config default.</param>
    /// <param name="participantId">Client-generated participant ID for filtering, or null.</param>
    /// <param name="audioStream">The raw audio stream (16 kHz, 16-bit, mono PCM).</param>
    /// <param name="ct">Cancellation token to abort the session.</param>
    public async Task ProcessAudioSessionAsync(string sessionId, string? groupId, string? sourceLanguage, string? participantId, Stream audioStream, CancellationToken ct)
    {
        var effectiveSourceLang = sourceLanguage ?? _configuration["Speech:SourceLanguage"] ?? "en-US";

        // Compute target languages from active participants (minus the source language)
        var activeLanguages = groupId != null
            ? _participantRegistry.GetActiveLanguageCodes(groupId)
            : [];

        var sourceLangCode = ToShortCode(effectiveSourceLang);
        // Target languages = active participant languages minus the speaker's own language
        var targetLanguages = activeLanguages
            .Where(l => !l.Equals(sourceLangCode, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Fall back to config if no other participants have joined yet
        if (targetLanguages.Length == 0)
        {
            targetLanguages = _configuration.GetSection("Speech:TargetLanguages").Get<string[]>() ?? [];
            _logger.LogInformation("No other participant languages yet — falling back to config targets: {Targets}",
                string.Join(", ", targetLanguages));
        }

        _logger.LogInformation("Starting audio processing for session {SessionId} (source={Source}, participant={Participant}, targets={Targets})",
            sessionId, effectiveSourceLang, participantId ?? "unknown", string.Join(", ", targetLanguages));

        try
        {
            await _speechTranslation.StartTranslationAsync(
                audioStream,
                sessionId,
                effectiveSourceLang,
                targetLanguages,
                async result => await OnTranslationResultAsync(sessionId, groupId, effectiveSourceLang, participantId, result),
                ct);

            // Keep the session alive until cancellation is requested
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Audio session {SessionId} was canceled", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing audio session {SessionId}", sessionId);
        }
        finally
        {
            await _speechTranslation.StopTranslationAsync(sessionId);
            _logger.LogInformation("Audio session {SessionId} ended", sessionId);
        }
    }

    private async Task OnTranslationResultAsync(string sessionId, string? groupId, string sourceLanguage, string? participantId, TranslationResult result)
    {
        // Determine which languages currently have active listeners
        var activeLanguages = groupId != null
            ? _participantRegistry.GetActiveLanguageCodes(groupId)
            : null;

        var publishTasks = new List<Task>();

        var sourceLangCode = ToShortCode(sourceLanguage);
        var sourceCaption = new CaptionMessage
        {
            SessionId = sessionId,
            ParticipantId = participantId ?? string.Empty,
            Language = sourceLangCode,
            Text = result.OriginalText,
            OriginalLanguage = sourceLanguage,
            OriginalText = result.OriginalText,
            Timestamp = result.Timestamp,
            IsFinal = result.IsFinal
        };
        publishTasks.Add(PublishCaptionSafeAsync(sourceCaption));

        // Publish each translated language — only for languages with active participants
        foreach (var (language, translatedText) in result.Translations)
        {
            // Skip languages that have no active listeners
            if (activeLanguages != null && !activeLanguages.Contains(language))
                continue;

            var caption = new CaptionMessage
            {
                SessionId = sessionId,
                ParticipantId = participantId ?? string.Empty,
                Language = language,
                Text = translatedText,
                OriginalLanguage = sourceLanguage,
                OriginalText = result.OriginalText,
                Timestamp = result.Timestamp,
                IsFinal = result.IsFinal
            };

            publishTasks.Add(PublishCaptionSafeAsync(caption));
        }

        await Task.WhenAll(publishTasks);

        // Synthesize and publish TTS audio only for languages with active listeners (final results only).
        // Fire-and-forget so TTS doesn't block the next translation callback.
        if (result.IsFinal)
        {
            foreach (var (ttsLanguage, ttsText) in result.Translations)
            {
                if (string.IsNullOrWhiteSpace(ttsText))
                    continue;

                // Skip TTS for the source language — the speaker already said it
                if (ttsLanguage.Equals(sourceLangCode, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip TTS for languages that have no active listeners
                if (activeLanguages != null && !activeLanguages.Contains(ttsLanguage))
                    continue;

                _ = SynthesizeAndPublishAsync(sessionId, participantId, ttsText, ttsLanguage);
            }
        }
    }

    private async Task PublishCaptionSafeAsync(CaptionMessage caption)
    {
        try
        {
            await _captionPublisher.PublishCaptionAsync(caption);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish caption for session {SessionId}, language {Language}",
                caption.SessionId, caption.Language);
        }
    }

    /// <summary>
    /// Runs TTS synthesis and publishes the audio clip. Called fire-and-forget
    /// so that translation callbacks are never blocked by TTS latency.
    /// </summary>
    private async Task SynthesizeAndPublishAsync(string sessionId, string? participantId, string text, string language)
    {
        _logger.LogInformation("[{SessionId}] Starting TTS for \"{Text}\" → {Language}",
            sessionId, text[..Math.Min(text.Length, 80)], language);
        try
        {
            var audioData = await _textToSpeech.SynthesizeAsync(text, language);
            _logger.LogInformation("[{SessionId}] TTS returned {Bytes} bytes", sessionId, audioData.Length);
            if (audioData.Length > 0)
            {
                await _captionPublisher.PublishAudioAsync(language, audioData, participantId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TTS synthesis/publish failed for session {SessionId}", sessionId);
        }
    }

    /// <summary>Converts "fr-FR" → "fr", maps "zh-CN" → "zh-Hans", keeps "zh-Hans" intact.</summary>
    private static string ToShortCode(string bcp47)
    {
        if (bcp47.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
        {
            // Already in script-subtag form (zh-Hans, zh-Hant)
            if (bcp47.Length >= 7 && !char.IsUpper(bcp47[3]))
                return bcp47[..7];

            // Map region codes to script subtags
            return bcp47.ToUpperInvariant() switch
            {
                "ZH-CN" or "ZH-SG" => "zh-Hans",
                "ZH-TW" or "ZH-HK" => "zh-Hant",
                _ => "zh-Hans",
            };
        }

        return bcp47.Split('-')[0];
    }
}
