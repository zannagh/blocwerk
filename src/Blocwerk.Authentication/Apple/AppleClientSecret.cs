// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// The client secret Apple's token endpoint expects: a JWT signed with ES256 by the Sign in with Apple key, with
/// <c>iss</c> the team id, <c>sub</c> the Services ID and <c>aud</c> <c>https://appleid.apple.com</c>.
/// </summary>
/// <remarks>
/// Apple accepts at most six months of lifetime. One secret lives <see cref="Lifetime"/> and is made again once less
/// than <see cref="RenewBefore"/> is left, so a secret in use never expires mid-exchange.
/// </remarks>
public sealed class AppleClientSecret
{
    /// <summary>The audience of every client secret.</summary>
    public const string Audience = "https://appleid.apple.com";

    /// <summary>How long one secret is valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>How long before its expiry a secret is replaced.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(1);

    private readonly AppleWebSettings settings;
    private readonly TimeProvider clock;
    private readonly ECDsa? key;
    private readonly Lock gate = new();
    private string? current;
    private DateTimeOffset currentExpiry;

    public AppleClientSecret(AppleWebSettings settings, TimeProvider? clock = null, ILogger<AppleClientSecret>? logger = null)
    {
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        key = settings.IsConfigured ? AppleSigningKey.TryLoad(settings.PrivateKey) : null;
        if (settings.IsConfigured && key is null)
        {
            // Never the value: only that it is unusable, so the provider stays off.
            logger?.LogWarning(
                "{Variable} is set but is no P-256 private key; Sign in with Apple stays off",
                AppleWebSettings.PrivateKeyVariable);
        }
    }

    /// <summary>Gets a value indicating whether the configured private key loaded.</summary>
    public bool HasKey => key is not null;

    /// <summary>A valid client secret, or null when no usable key is configured.</summary>
    public string? Get()
    {
        if (key is null)
        {
            return null;
        }

        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (current is null || currentExpiry - now < RenewBefore)
            {
                currentExpiry = now + Lifetime;
                current = Create(key, now, currentExpiry);
            }

            return current;
        }
    }

    private string Create(ECDsa signingKey, DateTimeOffset issuedAt, DateTimeOffset expires)
    {
        var credentials = new SigningCredentials(
            new ECDsaSecurityKey(signingKey) { KeyId = settings.KeyId },
            SecurityAlgorithms.EcdsaSha256);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.TeamId,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, settings.ServicesId)]),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = credentials,
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }
}
