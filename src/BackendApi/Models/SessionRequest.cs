namespace BackendApi.Models;

public class SessionRequest
{
    public string CallId { get; set; } = string.Empty;
    public string[] TargetLanguages { get; set; } = Array.Empty<string>();
}
