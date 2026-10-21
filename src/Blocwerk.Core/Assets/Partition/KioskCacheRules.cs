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

    /// <summary>The wall a kiosk session caches, or null when it caches none.</summary>
    public static Guid? CachedWallId(IKioskContext kioskContext) =>
        KioskViewing.ViewableWallId(kioskContext) is { } wallId && AllowsKioskCache(kioskContext, wallId) ? wallId : null;
}
