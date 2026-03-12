namespace KioskClient.Models;

public class TokenInfo
{
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset ExpiresOn { get; set; }
    public string UserId { get; set; } = string.Empty;
}
