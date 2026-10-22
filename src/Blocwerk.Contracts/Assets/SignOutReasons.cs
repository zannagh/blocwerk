// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// Why a request carries no signed-in session (Phase 3 plan §1, the 401 lock). A device that cached a <c>u-</c> partition
/// locks it on <see cref="Expired"/> (the same user signing in again unlocks it) and wipes it on any other reason.
/// </summary>
/// <remarks>
/// It reaches the device through: the <c>data-signout</c> attribute of an <c>anon</c> page's <c>bw-partition</c> meta,
/// <c>signOut</c> in <c>/api/me/cache-partition</c>, the <see cref="HeaderName"/> header on every 401 under <c>/api</c>,
/// and <c>reason</c> in the feed's 401 body. A 410 (<c>account-gone</c>) always wipes and carries none.
/// </remarks>
public static class SignOutReasons
{
    /// <summary>The header on a 401 under <c>/api</c>.</summary>
    public const string HeaderName = "X-Blocwerk-SignOut";

    /// <summary>The attribute of the <c>bw-partition</c> meta on an <c>anon</c> page.</summary>
    public const string MetaAttribute = "data-signout";

    /// <summary>The ticket ran out, or no session cookie arrived at all (the browser dropped an expired one).</summary>
    public const string Expired = "expired";

    /// <summary>The server ended the session: a kiosk or API-key session validator rejected it.</summary>
    public const string Forced = "forced";

    /// <summary>The session names an account that is gone: erased, disabled, merged away, or a system row.</summary>
    public const string Gone = "gone";

    /// <summary>True for the reasons a device may carry over a request (the server's sticky marker).</summary>
    public static bool IsSticky(string? reason) => reason is Forced or Gone;
}
