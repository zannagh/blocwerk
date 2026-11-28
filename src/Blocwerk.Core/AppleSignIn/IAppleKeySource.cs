// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.IdentityModel.Tokens;

namespace Blocwerk.Core.AppleSignIn;

/// <summary>Apple's published signing keys.</summary>
public interface IAppleKeySource
{
    /// <summary>
    /// The current keys. When none has <paramref name="keyId"/>, the keys are fetched again, unless a fetch ran within
    /// the refresh interval: then the cached keys come back and the token simply fails to verify.
    /// </summary>
    Task<IReadOnlyList<SecurityKey>> GetKeysAsync(string? keyId, CancellationToken ct = default);
}
