// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Assets.Partition;

/// <summary>
/// The one answer to "may this kiosk tablet keep an asset cache of its wall?" (design §6.6, decisions 4 and 12). A tablet
/// caches only the wall it is bound to, and only when the tablet with nobody picked may view that wall: every identity
/// it can take then may view what is cached, so one partition serves them all. Otherwise the tablet gets the
/// <c>none</c> partition and no manifest, so nothing is cached and an act-as release wipes.
/// </summary>
/// <remarks>
/// Today a bound tablet may always view its wall anonymously (<see cref="KioskViewing"/>), so the refusal covers a
/// tablet whose wall is unknown (re-registered mid-session) or another wall. A per-wall "no anonymous kiosk viewing"
/// switch, if one is ever added, belongs here and nowhere else.
/// </remarks>
public static class KioskCacheRules
{
    /// <summary>True when the kiosk session may cache <paramref name="wallId"/>'s wall-level assets.</summary>
    public static bool AllowsKioskCache(IKioskContext kioskContext, Guid wallId) =>
        kioskContext.KioskApiKeyId is not null && KioskViewing.AllowsAnonymousViewOf(kioskContext, wallId);

    /// <summary>
    /// The wall a kiosk session caches, or null when it caches none: also null once its key is revoked or re-bound
    /// (<see cref="IKioskKeyValidator"/>), and null without a validator. A revoked tablet's partition is then
    /// <c>none</c>, which wipes its page-side caches.
    /// </summary>
    public static async Task<Guid?> CachedWallIdAsync(IKioskContext kioskContext, IKioskKeyValidator? keys, CancellationToken ct) =>
        KioskViewing.ViewableWallId(kioskContext) is { } wallId && await AllowsKioskCacheAsync(kioskContext, wallId, keys, ct) ? wallId : null;

    /// <summary><see cref="AllowsKioskCache"/>, and the tablet's key is still live for the wall.</summary>
    public static async Task<bool> AllowsKioskCacheAsync(IKioskContext kioskContext, Guid wallId, IKioskKeyValidator? keys, CancellationToken ct) =>
        AllowsKioskCache(kioskContext, wallId) && await KeyIsLiveAsync(kioskContext, wallId, keys, ct);

    /// <summary>
    /// Whether the kiosk session's key is still live for <paramref name="wallId"/>, as <c>FeedAccessGate</c> asks it: a
    /// revoked or re-bound key stops every asset, manifest, document, partition and summary read on its next request,
    /// not at cookie expiry. No validator means no grant.
    /// </summary>
    public static async Task<bool> KeyIsLiveAsync(IKioskContext kioskContext, Guid wallId, IKioskKeyValidator? keys, CancellationToken ct) =>
        kioskContext.KioskApiKeyId is { } keyId && keyId != Guid.Empty && keys is not null && await keys.IsKeyValidAsync(keyId, wallId, ct);
}
