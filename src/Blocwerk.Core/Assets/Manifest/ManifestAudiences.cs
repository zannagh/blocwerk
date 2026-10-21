// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Assets.Resolution;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>Maps a caller's <see cref="AssetAccess"/> onto its <see cref="ManifestAudience"/>.</summary>
public static class ManifestAudiences
{
    /// <summary>The caller's class, or null when the caller gets no manifest (the route answers 404).</summary>
    /// <param name="access">The caller's grant on the wall, from <see cref="AssetAccessGate"/>.</param>
    /// <param name="kioskContext">The session's kiosk state, if any.</param>
    /// <param name="wallId">The wall.</param>
    public static ManifestAudience? From(AssetAccess access, IKioskContext? kioskContext, Guid wallId)
    {
        if (access == AssetAccess.None)
        {
            return null;
        }

        if (kioskContext is { IsKiosk: true })
        {
            return KioskCacheRules.AllowsKioskCache(kioskContext, wallId) ? ManifestAudience.Kiosk : null;
        }

        return access switch
        {
            AssetAccess.Member => ManifestAudience.Member,
            AssetAccess.ShareToken => ManifestAudience.ShareToken,
            _ => null,
        };
    }
}
