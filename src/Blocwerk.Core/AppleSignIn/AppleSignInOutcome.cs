// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>How a Sign in with Apple from the companion app went.</summary>
public enum AppleSignInOutcome
{
    /// <summary>A device key was minted for the linked account.</summary>
    SignedIn,

    /// <summary>The token, nonce or device name did not verify.</summary>
    InvalidGrant,

    /// <summary>The token verified, but no live account here is linked to its Apple ID.</summary>
    NotLinked,
}
