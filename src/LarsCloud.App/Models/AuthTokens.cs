namespace LarsCloud.Models;

public sealed class AuthTokens
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
