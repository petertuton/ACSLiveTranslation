namespace MediaProcessor.Models;

/// <summary>
/// A caption message destined for a specific language group via Web PubSub.
/// </summary>
public class CaptionMessage
{
    /// <summary>
    /// Unique identifier for the audio/call session that produced this caption.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// The client-generated participant identifier. Used by clients to filter out
    /// their own messages so the speaker doesn't hear their own translation.
    /// </summary>
    public string ParticipantId { get; set; } = string.Empty;

    /// <summary>
    /// The target language code this caption is translated into (e.g., "es", "fr").
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The translated caption text in the target language.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// The BCP-47 language code of the original spoken language.
    /// </summary>
    public string OriginalLanguage { get; set; } = string.Empty;

    /// <summary>
    /// The original recognized text before translation.
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// The timestamp when this caption was generated.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Indicates whether this caption corresponds to a final recognition result.
    /// </summary>
    public bool IsFinal { get; set; }
}
