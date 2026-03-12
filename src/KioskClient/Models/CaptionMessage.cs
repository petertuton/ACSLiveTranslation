namespace KioskClient.Models;

public class CaptionMessage
{
    public string SessionId { get; set; } = string.Empty;
    public string ParticipantId { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string OriginalLanguage { get; set; } = string.Empty;
    public string OriginalText { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public bool IsFinal { get; set; }
    public bool IsSystem { get; set; }
}
