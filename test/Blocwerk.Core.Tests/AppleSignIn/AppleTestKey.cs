// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Core.AppleSignIn;
using Blocwerk.Core.DeviceLinks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>A stand-in for one of Apple's signing keys: mints identity tokens and publishes itself as a JWKS entry.</summary>
internal sealed class AppleTestKey
{
    public AppleTestKey(string keyId)
    {
        Rsa = RSA.Create(2048);
        Key = new RsaSecurityKey(Rsa) { KeyId = keyId };
    }

    public RSA Rsa { get; }

    public RsaSecurityKey Key { get; }

    /// <summary>The JWKS document Apple would serve with <paramref name="keys"/>.</summary>
    public static string Jwks(params AppleTestKey[] keys) => JsonSerializer.Serialize(new
    {
        keys = keys.Select(k =>
        {
            var p = k.Rsa.ExportParameters(false);
            return new Dictionary<string, string>
            {
                ["kty"] = "RSA",
                ["kid"] = k.Key.KeyId,
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["n"] = Base64UrlEncoder.Encode(p.Modulus),
                ["e"] = Base64UrlEncoder.Encode(p.Exponent),
            };
        }),
    });

    /// <summary>A token as Apple issues it for the app, with <paramref name="nonce"/> hashed into it; each part overridable.</summary>
    public string Mint(
        string subject,
        string nonce,
        DateTimeOffset now,
        string? audience = AppleSignInSettings.IosBundleId,
        string issuer = AppleIdentityTokenValidator.Issuer,
        TimeSpan? lifetime = null,
        string? rawNonceClaim = null)
    {
        var issued = now.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject), new Claim("nonce", rawNonceClaim ?? DeviceLinkPkce.Hash(nonce))]),
            IssuedAt = issued,
            NotBefore = issued.AddHours(-2),
            Expires = issued + (lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }
}
