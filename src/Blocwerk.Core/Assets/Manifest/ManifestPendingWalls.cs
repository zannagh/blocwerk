// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// The walls whose manifest lists a row without a hash: the rows the backfill still has to reach (or whose file is
/// missing). Metadata only; <c>IS NOT NULL</c> on a blob reads no bytes.
/// </summary>
internal static class ManifestPendingWalls
{
    public static async Task<HashSet<Guid>> QueryAsync(BlocwerkDbContext db, CancellationToken ct)
    {
        var walls = new HashSet<Guid>();
        walls.UnionWith(await db.WallPanels.IgnoreQueryFilters()
            .Where(p => p.Photo != null && p.PhotoSha256 == null).Select(p => p.WallId).Distinct().ToListAsync(ct));
        walls.UnionWith(await db.Walls.IgnoreQueryFilters()
            .Where(w => w.Photo != null && w.PhotoSha256 == null).Select(w => w.Id).ToListAsync(ct));
        walls.UnionWith(await db.WallResets.IgnoreQueryFilters()
            .Where(r => r.PreviousPhoto != null && r.PreviousPhotoSha256 == null).Select(r => r.WallId).Distinct().ToListAsync(ct));
        walls.UnionWith(await db.WallImages.IgnoreQueryFilters()
            .Where(i => i.Sha256 == null).Select(i => i.WallId).Distinct().ToListAsync(ct));
        walls.UnionWith(await db.WallGeometryTextures.IgnoreQueryFilters()
            .Where(t => t.GeometryModel.IsActive && (t.Sha256 == null || (t.MaskStoredPath != null && t.MaskSha256 == null)))
            .Select(t => t.GeometryModel.WallId).Distinct().ToListAsync(ct));
        walls.UnionWith(await db.WallGeometrySplats.IgnoreQueryFilters()
            .Where(s => s.GeometryModel.IsActive && (s.Sha256 == null || (s.MobileStoredPath != null && s.MobileSha256 == null) || s.LodLevelsJson != null))
            .Select(s => s.GeometryModel.WallId).Distinct().ToListAsync(ct));
        walls.UnionWith(await db.BetaVideos.IgnoreQueryFilters()
            .Where(v => v.Thumbnail != null && v.ThumbnailSha256 == null).Select(v => v.Boulder.WallId).Distinct().ToListAsync(ct));
        return walls;
    }
}
