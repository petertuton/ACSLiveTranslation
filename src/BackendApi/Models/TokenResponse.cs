namespace BackendApi.Models;

public class TokenResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset ExpiresOn { get; set; }
    public string UserId { get; set; } = string.Empty;
}
