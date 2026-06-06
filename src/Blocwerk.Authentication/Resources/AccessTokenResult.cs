using System.Text.Json.Serialization;

namespace Blocwerk.Authentication.Resources;

public class AccessTokenResult
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public required string TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; set; }
}
