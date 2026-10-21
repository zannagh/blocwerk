// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State;

/// <summary>
/// What <see cref="WallCacheState.Changed"/> reports: ids only, never entity graphs. A component that shows one of them
/// reloads it through the cache.
/// </summary>
/// <param name="Scope">Which kind of entry went.</param>
/// <param name="WallId">The wall involved, or <see cref="Guid.Empty"/> for the wall list.</param>
/// <param name="BoulderId">The boulder involved, or <see cref="Guid.Empty"/> when not applicable.</param>
public readonly record struct WallCacheChange(WallCacheChangeScope Scope, Guid WallId, Guid BoulderId)
{
    public static WallCacheChange OfWall(Guid wallId) => new(WallCacheChangeScope.Wall, wallId, Guid.Empty);

    public static WallCacheChange OfBoulder(Guid wallId, Guid boulderId) => new(WallCacheChangeScope.Boulder, wallId, boulderId);

    public static WallCacheChange WallList { get; } = new(WallCacheChangeScope.WallList, Guid.Empty, Guid.Empty);
}
