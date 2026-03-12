namespace MediaProcessor.Models;

/// <summary>
/// Represents the result of a speech-to-text translation operation,
/// containing the original text and its translations into target languages.
/// </summary>
public class TranslationResult
{
    /// <summary>
    /// The recognized text in the source language.
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// The BCP-47 language code of the source language (e.g., "en-US").
    /// </summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>
    /// A dictionary mapping target language codes to their translated text.
    /// </summary>
    public Dictionary<string, string> Translations { get; set; } = new();

    /// <summary>
    /// The timestamp when the translation was produced.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Indicates whether this is a final (as opposed to partial/interim) recognition result.
    /// </summary>
    public bool IsFinal { get; set; }
}
