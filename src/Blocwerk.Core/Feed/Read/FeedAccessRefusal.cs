// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>Why a feed read was refused; the web layer answers each with its own status (<c>FeedRefusals</c>).</summary>
public enum FeedAccessRefusal
{
    /// <summary>Granted.</summary>
    None = 0,

    /// <summary>Nobody is signed in, and no share token or kiosk binding grants the wall (401).</summary>
    Unauthenticated = 1,

    /// <summary>The caller is known but may not read this wall, or this route (403).</summary>
    NoAccess = 2,

    /// <summary>The session names an account that no longer exists, or may not sign in (410).</summary>
    AccountGone = 3,
}
