// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>Verifies an identity token the native app received from Sign in with Apple.</summary>
public interface IAppleIdentityTokenValidator
{
    /// <summary>
    /// The token's subject, or null unless it is signed by one of Apple's keys with RS256, issued by Apple for one of
    /// the allowed audiences, unexpired, and its <c>nonce</c> claim is the SHA-256 of <paramref name="nonce"/>.
    /// Checks the token only: whether the nonce is one the server issued and still unused is the caller's step.
    /// </summary>
    Task<AppleIdentity?> ValidateAsync(string? identityToken, string? nonce, CancellationToken ct = default);
}
