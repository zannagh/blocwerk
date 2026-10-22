// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// Serves <c>GET /api/me/cache-summary</c>: the viewer's cacheable walls, for the client's fill queue, and the count of them
/// with 3D data, for the client's high-storage banner. The walls are the ones the viewer's manifests would list 3D for: a signed-in caller's walls through the same
/// membership filter <see cref="Resolution.AssetAccessGate"/> grants on, a kiosk tablet's bound wall only when
/// <see cref="KioskCacheRules"/> lets it cache, and none for anybody else.
/// </summary>
public sealed class CacheSummaryService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IKioskContext? kioskContext = null,
    IKioskKeyValidator? kioskKeys = null)
{
    public async Task<CacheSummaryResponse> GetAsync(CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var walls = await WallsAsync(db, ct);
        if (walls.Count == 0)
        {
            return new CacheSummaryResponse(0, []);
        }

        // "Has 3D data" is what ManifestGeometryEntries lists: the active model's textures or its splat.
        var count = await db.WallGeometryModels.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.IsActive && walls.Contains(m.WallId)
                && (db.WallGeometryTextures.Any(t => t.GeometryModelId == m.Id) || db.WallGeometrySplats.Any(s => s.GeometryModelId == m.Id)))
            .Select(m => m.WallId)
            .Distinct()
            .CountAsync(ct);
        return new CacheSummaryResponse(count, walls);
    }

    private async Task<List<Guid>> WallsAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        if (kioskContext is not null)
        {
            await kioskContext.InitializeAsync();
            if (kioskContext.IsKiosk)
            {
                return await KioskCacheRules.CachedWallIdAsync(kioskContext, kioskKeys, ct) is { } wallId ? [wallId] : [];
            }
        }

        try
        {
            db.CurrentUserId = (await currentUserService.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return await db.Walls.AsNoTracking().Select(w => w.Id).ToListAsync(ct);
    }
}
