// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Resolution;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The variant-cache key of a hashed asset's rendition WITH the legacy keys its row's routes cached renditions under,
/// so the first <c>/a/{sha}/{width}</c> after a deploy moves the existing rendition into the sha slot instead of
/// rendering it again. The legacy keys are built exactly as the legacy routes and <c>ImageWarmTargets</c> build them,
/// from the row's metadata (one small projection, never the bytes). A row the lookup cannot read gets the sha key
/// alone, which still serves (it renders, under the render limit).
/// </summary>
public static class AssetLegacyVariantKeys
{
    /// <summary>The key for <paramref name="asset"/>'s renditions: its sha, plus every legacy key of the row.</summary>
    public static async Task<ImageVariantKey> ForAsync(IDbContextFactory<BlocwerkDbContext> factory, ResolvedAsset asset, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var legacy = asset.Kind switch
        {
            AssetKind.Panel => await PanelAsync(db, asset.RowId, ct),
            AssetKind.WallPhoto => await WallPhotoAsync(db, asset.RowId, ct),
            AssetKind.ResetPhoto => await ResetPhotoAsync(db, asset.RowId, ct),
            AssetKind.Gallery => await GalleryAsync(db, asset.RowId, ct),
            _ => [],
        };

        return legacy.Count == 0
            ? new ImageVariantKey(string.Empty, string.Empty, asset.Hex)
            : new ImageVariantKey(legacy[0].Identity, legacy[0].Version, asset.Hex, [.. legacy.Skip(1).Select(k => (k.Identity, k.Version))]);
    }

    private static async Task<List<ImageVariantKey>> PanelAsync(BlocwerkDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.WallPanels.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == id && p.Photo != null)
            .Select(p => new { p.Generation, p.PhotoRevision, Length = p.Photo!.Length, p.PhotoContentType })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? []
            : [ImageResponse.VariantKey(
                new WallPhotoTag(row.Length, row.PhotoContentType, PanelPhotoVersion.Of(row.Generation, row.PhotoRevision), IsArchived: false),
                ImageIdentity.PanelPhoto(id, ImageIdentity.LiveSlot))];
    }

    private static async Task<List<ImageVariantKey>> WallPhotoAsync(BlocwerkDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == id && w.Photo != null)
            .Select(w => new { w.CurrentGeneration, Length = w.Photo!.Length, w.PhotoContentType })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return [];
        }

        var tag = new WallPhotoTag(row.Length, row.PhotoContentType, row.CurrentGeneration, IsArchived: false);
        return
        [
            ImageResponse.VariantKey(tag, ImageIdentity.WallPhoto(id)),
            ImageResponse.VariantKey(tag, ImageIdentity.WallGenerationPhoto(id, row.CurrentGeneration)),
            ImageResponse.VariantKey(tag, ImageIdentity.LegacyGalleryImage(id, WallGallerySource.WallPhoto, id)),
        ];
    }

    private static async Task<List<ImageVariantKey>> ResetPhotoAsync(BlocwerkDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.WallResets.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Id == id && r.PreviousPhoto != null)
            .Select(r => new { r.WallId, r.Generation, Length = r.PreviousPhoto!.Length, r.PreviousPhotoContentType })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return [];
        }

        var tag = new WallPhotoTag(row.Length, row.PreviousPhotoContentType, row.Generation, IsArchived: true);
        return
        [
            ImageResponse.VariantKey(tag, ImageIdentity.WallGenerationPhoto(row.WallId, row.Generation)),
            ImageResponse.VariantKey(tag, ImageIdentity.LegacyGalleryImage(row.WallId, WallGallerySource.ResetPhoto, id)),
        ];
    }

    private static async Task<List<ImageVariantKey>> GalleryAsync(BlocwerkDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.WallImages.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new { i.SizeBytes, i.CapturedAt, i.ContentType })
            .FirstOrDefaultAsync(ct);
        return row is null ? [] : [ImageResponse.UploadedGalleryKey(id, row.SizeBytes, row.CapturedAt, row.ContentType, null)];
    }
}
