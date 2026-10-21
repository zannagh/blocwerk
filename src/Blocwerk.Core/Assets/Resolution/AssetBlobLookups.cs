// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// The database-blob kinds of <see cref="AssetResolver"/>: each lookup is scoped by the wall and matches on the hash
/// column, and projects metadata only (a server-side length, never the bytes). The caller has passed
/// <see cref="AssetAccessGate"/> already, so the wall query filter is bypassed where it would get in the way.
/// </summary>
internal static class AssetBlobLookups
{
    private const string Jpeg = "image/jpeg";

    public static async Task<ResolvedAsset?> PanelAsync(BlocwerkDbContext db, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var row = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null && p.PhotoSha256 == sha)
            .Select(p => new { p.Id, p.PhotoContentType, Length = p.Photo!.Length })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new ResolvedAsset(AssetKind.Panel, row.Id, sha, row.PhotoContentType ?? Jpeg, row.Length);
    }

    public static async Task<ResolvedAsset?> WallPhotoAsync(BlocwerkDbContext db, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var row = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId && w.Photo != null && w.PhotoSha256 == sha)
            .Select(w => new { w.Id, w.PhotoContentType, Length = w.Photo!.Length })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new ResolvedAsset(AssetKind.WallPhoto, row.Id, sha, row.PhotoContentType ?? Jpeg, row.Length);
    }

    public static async Task<ResolvedAsset?> ResetPhotoAsync(BlocwerkDbContext db, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var row = await db.WallResets.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.WallId == wallId && r.PreviousPhoto != null && r.PreviousPhotoSha256 == sha)
            .Select(r => new { r.Id, r.PreviousPhotoContentType, Length = r.PreviousPhoto!.Length })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new ResolvedAsset(AssetKind.ResetPhoto, row.Id, sha, row.PreviousPhotoContentType ?? Jpeg, row.Length);
    }

    /// <summary>
    /// A beta video's poster frame, under the legacy thumbnail route's rules: a share-token viewer never sees one of
    /// a draft boulder, and an anonymous kiosk sees none (that route needs a signed-in member or a token).
    /// </summary>
    public static async Task<ResolvedAsset?> VideoThumbnailAsync(
        BlocwerkDbContext db, Guid wallId, byte[] sha, AssetAccess access, CancellationToken ct)
    {
        if (access is not (AssetAccess.Member or AssetAccess.ShareToken))
        {
            return null;
        }

        var drafts = access == AssetAccess.Member;
        var row = await db.BetaVideos.AsNoTracking()
            .Where(v => v.Boulder.WallId == wallId && (drafts || !v.Boulder.IsDraft)
                && v.Thumbnail != null && v.ThumbnailSha256 == sha)
            .Select(v => new { v.Id, Length = v.Thumbnail!.Length })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new ResolvedAsset(AssetKind.VideoThumbnail, row.Id, sha, Jpeg, row.Length);
    }

    /// <summary>
    /// The blob of a resolved row, read only while the row's hash column still holds the resolved sha: hash and bytes
    /// come from one row version, so a rewrite in between yields null rather than the new bytes under the old sha.
    /// </summary>
    public static Task<byte[]?> LoadAsync(BlocwerkDbContext db, ResolvedAsset asset, CancellationToken ct)
    {
        var id = asset.RowId;
        var sha = asset.Sha256;
        return asset.Kind switch
        {
            AssetKind.Panel => db.WallPanels.AsNoTracking()
                .Where(p => p.Id == id && p.PhotoSha256 == sha).Select(p => p.Photo).FirstOrDefaultAsync(ct),
            AssetKind.WallPhoto => db.Walls.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.Id == id && w.PhotoSha256 == sha).Select(w => w.Photo).FirstOrDefaultAsync(ct),
            AssetKind.ResetPhoto => db.WallResets.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.Id == id && r.PreviousPhotoSha256 == sha).Select(r => r.PreviousPhoto).FirstOrDefaultAsync(ct),
            AssetKind.VideoThumbnail => db.BetaVideos.AsNoTracking()
                .Where(v => v.Id == id && v.ThumbnailSha256 == sha).Select(v => v.Thumbnail).FirstOrDefaultAsync(ct),
            _ => Task.FromResult<byte[]?>(null),
        };
    }
}
