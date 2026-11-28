// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>
/// Makes and reads the server's Sign in with Apple nonces without storing them: a nonce carries random bytes and its
/// expiry, signed with a server key, so only this server can make one and nobody can extend one.
/// </summary>
public interface IAppleNonceCodec
{
    /// <summary>A new nonce that expires at <paramref name="expiresAt"/>.</summary>
    string Issue(DateTimeOffset expiresAt);

    /// <summary>True for a nonce this server made, with its expiry; false for anything forged, altered or garbled.</summary>
    bool TryRead(string nonce, out DateTimeOffset expiresAt);
}
