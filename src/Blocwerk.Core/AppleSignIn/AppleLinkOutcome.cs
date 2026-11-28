// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>How linking an Apple ID to the caller's account went.</summary>
public enum AppleLinkOutcome
{
    /// <summary>The Apple ID is now linked to the account (or already was).</summary>
    Linked,

    /// <summary>The token or nonce did not verify, or the nonce was not issued, expired or already used.</summary>
    InvalidToken,

    /// <summary>The Apple ID is already linked to another account on this instance.</summary>
    LinkedElsewhere,
}
