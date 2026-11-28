// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.DeviceLinks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Core.AppleSignIn;

/// <inheritdoc cref="IAppleIdentityTokenValidator"/>
/// <remarks>
/// A native token needs no Apple private key: it is checked against Apple's public keys alone. The lifetime is checked
/// here rather than by the handler so it runs on the injected clock. Only the subject is taken from the token; its
/// e-mail is never used to find an account.
/// </remarks>
public sealed class AppleIdentityTokenValidator(
    IAppleKeySource keySource,
    AppleSignInSettings settings,
    ILogger<AppleIdentityTokenValidator> logger,
    TimeProvider? clock = null) : IAppleIdentityTokenValidator
{
    /// <summary>The issuer of every Apple identity token.</summary>
    public const string Issuer = "https://appleid.apple.com";

    /// <summary>The longest token looked at; anything longer is refused unread.</summary>
    public const int MaxTokenLength = 8192;

    /// <summary>The longest nonce looked at (a signed nonce is about 120 characters).</summary>
    public const int MaxNonceLength = 512;

    /// <summary>How far the token's times may be off this server's clock.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly JsonWebTokenHandler handler = new();

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<AppleIdentity?> ValidateAsync(
        string? identityToken,
        string? nonce,
        IReadOnlyCollection<string>? audiences = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(identityToken) || identityToken.Length > MaxTokenLength
            || string.IsNullOrEmpty(nonce) || nonce.Length > MaxNonceLength
            || !handler.CanReadToken(identityToken))
        {
            return null;
        }

        var jwt = new JsonWebToken(identityToken);
        var keys = await keySource.GetKeysAsync(jwt.Kid, ct);
        var result = await handler.ValidateTokenAsync(jwt, Parameters(keys, audiences ?? settings.AllowedAudiences));
        if (!result.IsValid)
        {
            logger.LogWarning("Apple identity token refused: {Reason}", result.Exception?.GetType().Name ?? "invalid");
            return null;
        }

        var refusal = CheckLifetime(jwt) ?? CheckNonce(jwt, nonce) ?? CheckSubject(jwt);
        if (refusal is not null)
        {
            logger.LogWarning("Apple identity token refused: {Reason}", refusal);
            return null;
        }

        return new AppleIdentity(jwt.Subject);
    }

    private static string? CheckNonce(JsonWebToken jwt, string nonce)
    {
        if (!jwt.TryGetPayloadValue<string>("nonce", out var claimed) || string.IsNullOrEmpty(claimed))
        {
            return "no nonce";
        }

        var expected = Encoding.ASCII.GetBytes(DeviceLinkPkce.Hash(nonce));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(claimed)) ? null : "nonce mismatch";
    }

    private static string? CheckSubject(JsonWebToken jwt) =>
        string.IsNullOrWhiteSpace(jwt.Subject) || jwt.Subject.Length > 255 ? "no usable subject" : null;

    private static TokenValidationParameters Parameters(IReadOnlyList<SecurityKey> keys, IReadOnlyCollection<string> audiences) => new()
    {
        ValidIssuer = Issuer,
        ValidAudiences = audiences,
        IssuerSigningKeys = keys,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        TryAllIssuerSigningKeys = false,
        ValidateLifetime = false,
    };

    private string? CheckLifetime(JsonWebToken jwt)
    {
        var now = Clock.GetUtcNow().UtcDateTime;
        if (!jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Exp, out _) || jwt.ValidTo + ClockSkew <= now)
        {
            return "expired or no expiry";
        }

        if (!jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Iat, out _) || jwt.IssuedAt - ClockSkew > now)
        {
            return "issued in the future or no issue time";
        }

        return null;
    }
}
