// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.AppleSignIn;

namespace Blocwerk.Core.Tests.AppleSignIn;

/// <summary>
/// A validator whose tokens are <c>ok:&lt;subject&gt;</c>: those verify (with any nonce), everything else does not. The
/// real checks have their own tests; this one lets the service's nonce, link and key logic be tested alone.
/// </summary>
internal sealed class StubAppleValidator : IAppleIdentityTokenValidator
{
    public static string Token(string subject) => "ok:" + subject;

    public Task<AppleIdentity?> ValidateAsync(string? identityToken, string? nonce, CancellationToken ct = default) =>
        Task.FromResult(identityToken is not null && identityToken.StartsWith("ok:", StringComparison.Ordinal) && !string.IsNullOrEmpty(nonce)
            ? new AppleIdentity(identityToken[3..])
            : null);
}
