// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// The values of the cache partition (design §6.6): the label a device's asset cache is filed under, reported by
/// <c>GET /api/me/cache-partition</c> and by the <c>bw-partition</c> meta of every server-rendered page. A label makes a
/// wipe precise; it is not a security boundary.
/// </summary>
public static class CachePartitions
{
    /// <summary>The meta's name.</summary>
    public const string MetaName = "bw-partition";

    /// <summary>A signed-in user: <c>u-</c> plus 20 base32 characters of an HMAC of the user id.</summary>
    public const string UserPrefix = "u-";

    /// <summary>A registered kiosk tablet, whoever it acts as: <c>k-</c> plus 20 base32 characters of an HMAC of its key.</summary>
    public const string KioskPrefix = "k-";

    /// <summary>No identity (share links included).</summary>
    public const string Anonymous = "anon";

    /// <summary>Cache nothing, and wipe what is cached (a kiosk whose wall allows no anonymous cache).</summary>
    public const string None = "none";

    /// <summary>
    /// No decision, keep the active partition: a page served from the device itself (<c>offline.html</c>), or a server
    /// page whose partition could not be resolved.
    /// </summary>
    public const string Offline = "offline";
}
