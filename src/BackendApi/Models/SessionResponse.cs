namespace BackendApi.Models;

public class SessionResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string CallId { get; set; } = string.Empty;
    public string WebPubSubUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
}
