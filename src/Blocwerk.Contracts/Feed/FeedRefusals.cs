// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>
/// The <see cref="FeedRefusal.Error"/> codes of the feed routes. Each has its own status, so an offline client can tell
/// them apart without parsing the body, and every one of them means "purge what this device holds" (design §7.2).
/// </summary>
public static class FeedRefusals
{
    /// <summary>401: nobody is signed in and nothing else grants the wall. Purge the per-user data; sign in again.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>
    /// 403: the caller is known but may not read this wall (not or no longer a member, a stale or rotated share link, a
    /// kiosk tablet of another wall, an API key). Purge that wall.
    /// </summary>
    public const string NoAccess = "no-access";

    /// <summary>
    /// 410: the session names an account that no longer exists (erased, merged away). Purge everything of that account.
    /// The response also drops the cookie, so a retry answers 401.
    /// </summary>
    public const string AccountGone = "account-gone";
}
