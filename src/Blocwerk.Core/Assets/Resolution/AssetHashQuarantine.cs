// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// What a confirmed hash/bytes mismatch does to its row: the hash column is set back to null, but only while it still
/// holds the sha that failed (<c>WHERE Id = @id AND hash = @sha</c>). The row then leaves the manifest and the hashed
/// URLs (<see cref="AssetUrls"/> falls back to the legacy route), and the backfill hashes it again on its next pass.
/// Without this a mismatch would fail closed for good: every request would 404 under a sha the row still claimed.
/// </summary>
/// <remarks>
/// One statement per column, with a <c>wall.manifest</c> row in the same transaction (<see cref="FeedBulkWrite"/>) so
/// cached manifests drop the asset. Never fatal: a failure is logged, and the request has its 404 either way.
/// </remarks>
internal static class AssetHashQuarantine
{
    private const string FeedSite = "AssetHashQuarantine";

    public static async Task ClearAsync(BlocwerkDbContext db, ResolvedAsset asset, ILogger logger)
    {
        try
        {
            var cleared = await FeedBulkWrite.RunAsync(
                db,
                FeedSite,
                () => WriteAsync(db, asset),
                count => count > 0 ? [FeedBulkRows.Manifest(asset.WallId, FeedAudience.Wall, actor: null)] : [],
                logger,
                CancellationToken.None);
            if (cleared > 0)
            {
                logger.LogWarning("{Kind} {RowId}: hash {Sha} cleared after a mismatch; the backfill hashes it again", asset.Kind, asset.RowId, asset.Hex);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not clear the mismatched hash {Sha} of {Kind} {RowId}", asset.Hex, asset.Kind, asset.RowId);
        }
    }

    private static Task<int> WriteAsync(BlocwerkDbContext db, ResolvedAsset asset)
    {
        var (id, sha) = (asset.RowId, asset.Sha256);
        return asset.Kind switch
        {
            AssetKind.Panel => db.WallPanels.Where(p => p.Id == id && p.PhotoSha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PhotoSha256, (byte[]?)null)),
            AssetKind.WallPhoto => db.Walls.IgnoreQueryFilters().Where(w => w.Id == id && w.PhotoSha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.PhotoSha256, (byte[]?)null)),
            AssetKind.ResetPhoto => db.WallResets.IgnoreQueryFilters().Where(r => r.Id == id && r.PreviousPhotoSha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.PreviousPhotoSha256, (byte[]?)null)),
            AssetKind.VideoThumbnail => db.BetaVideos.IgnoreQueryFilters().Where(v => v.Id == id && v.ThumbnailSha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(v => v.ThumbnailSha256, (byte[]?)null)),
            AssetKind.Gallery => db.WallImages.IgnoreQueryFilters().Where(i => i.Id == id && i.Sha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.Sha256, (byte[]?)null)),
            AssetKind.Texture => db.WallGeometryTextures.Where(t => t.Id == id && t.Sha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.Sha256, (byte[]?)null)),
            AssetKind.Mask => db.WallGeometryTextures.Where(t => t.Id == id && t.MaskSha256 == sha)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.MaskSha256, (byte[]?)null)),
            AssetKind.Splat => ClearSplatAsync(db, id, sha),
            _ => Task.FromResult(0),
        };
    }

    /// <summary>The full scene, the mobile copy, or a ladder level (its <c>sha</c> in the JSON, under a guard on the JSON).</summary>
    private static async Task<int> ClearSplatAsync(BlocwerkDbContext db, Guid id, byte[] sha)
    {
        var cleared = await db.WallGeometrySplats.Where(s => s.Id == id && s.Sha256 == sha)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Sha256, (byte[]?)null))
            + await db.WallGeometrySplats.Where(s => s.Id == id && s.MobileSha256 == sha)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.MobileSha256, (byte[]?)null));
        var hex = ContentHash.ToHex(sha)!;
        var ladder = await db.WallGeometrySplats.AsNoTracking().Where(s => s.Id == id).Select(s => s.LodLevelsJson).FirstOrDefaultAsync();
        var levels = SplatLodLadder.Parse(ladder);
        if (!levels.Any(l => string.Equals(l.Sha, hex, StringComparison.OrdinalIgnoreCase)))
        {
            return cleared;
        }

        var rewritten = SplatLodLadder.Serialize(
            [.. levels.Select(l => string.Equals(l.Sha, hex, StringComparison.OrdinalIgnoreCase) ? l with { Sha = null } : l)]);
        return cleared + await db.WallGeometrySplats.Where(s => s.Id == id && s.LodLevelsJson == ladder)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LodLevelsJson, rewritten));
    }
}
