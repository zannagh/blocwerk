// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Security.Cryptography;
using Blocwerk.Core.AppleSignIn;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>
/// Apple's side of the round trip: one RSA signing key (served as Apple's published keys) and identity tokens signed
/// with it, as Apple's token endpoint returns them.
/// </summary>
internal sealed class FakeApple : IAppleKeySource
{
    public const string KeyId = "fake-apple-key";

    private readonly RsaSecurityKey key = new(RSA.Create(2048)) { KeyId = KeyId };

    public Task<IReadOnlyList<SecurityKey>> GetKeysAsync(string? keyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SecurityKey>>([key]);

    /// <summary>
    /// An identity token for <paramref name="subject"/> carrying <paramref name="nonce"/> as Apple does: verbatim, the
    /// value the authorize request sent (the SHA-256 of the raw nonce).
    /// </summary>
    public string IdToken(string subject, string nonce, string audience = AppleWebTestKeys.ServicesId, string? email = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("nonce", nonce),
        };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = AppleIdentityTokenValidator.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }
}
