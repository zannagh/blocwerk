// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// Beta-video poster frames, the one kind whose grant depends on the caller (<c>AssetBlobLookups.VideoThumbnailAsync</c>):
/// a member sees every boulder's, a share-link viewer only those of boulders that are not drafts, and a kiosk none.
/// </summary>
internal static class ManifestVideoEntries
{
    public static async Task<List<ManifestAsset>> ThumbnailsAsync(BlocwerkDbContext db, Guid wallId, ManifestAudience audience, CancellationToken ct)
    {
        if (audience == ManifestAudience.Kiosk)
        {
            return [];
        }

        var drafts = audience == ManifestAudience.Member;
        var rows = await db.BetaVideos.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Boulder.WallId == wallId && (drafts || !v.Boulder.IsDraft) && v.Thumbnail != null)
            .Select(v => new { v.Id, v.BoulderId, v.ThumbnailSha256, Length = (long)v.Thumbnail!.Length })
            .ToListAsync(ct);
        return rows.Select(v => new ManifestAsset
        {
            Sha = ContentHash.ToHex(v.ThumbnailSha256),
            Kind = ManifestAssetKinds.VideoThumbnail,
            Id = v.Id,
            BoulderId = v.BoulderId,
            Type = "image/jpeg",
            Bytes = v.Length,
            Tier = ManifestTiers.Lazy,
        }).ToList();
    }
}
