using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Mints and hashes API key tokens. Tokens look like <c>bwk_&lt;64 hex chars&gt;</c> (32 random
/// bytes); only the hash is ever persisted, so a token exists in full exactly once.
/// </summary>
public static class ApiKeyTokens
{
    /// <summary>How much of the token is kept in clear as a display hint.</summary>
    private const int PrefixLength = 12;

    /// <summary>Creates a fresh token plus the display prefix that goes on the row.</summary>
    public static (string Token, string Prefix) Create()
    {
        var token = ApiKey.TokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return (token, token[..PrefixLength]);
    }

    /// <summary>Hex SHA-256 of a token, as stored in <see cref="ApiKey.KeyHash"/>.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>True when a bearer value is shaped like an API key rather than a JWT.</summary>
    public static bool LooksLikeApiKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith(ApiKey.TokenPrefix, StringComparison.Ordinal);
}
