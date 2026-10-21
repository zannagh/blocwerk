// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>
/// The file-backed kinds of <see cref="AssetResolver"/>: gallery uploads, facet textures and masks, and splat scenes.
/// The hash lives on the row next to the stored name; a file whose name escapes its store is not resolved.
/// </summary>
internal static class AssetFileLookups
{
    private const string SplatType = "application/octet-stream";

    public static async Task<ResolvedAsset?> GalleryAsync(
        BlocwerkDbContext db, IWallImageStorage storage, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var row = await db.WallImages.AsNoTracking()
            .Where(i => i.WallId == wallId && i.Sha256 == sha)
            .Select(i => new { i.Id, i.StoragePath, i.ContentType, i.SizeBytes })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : File(AssetKind.Gallery, row.Id, sha, row.ContentType, row.SizeBytes, storage.ResolvePhysicalPath(row.StoragePath));
    }

    public static async Task<ResolvedAsset?> TextureAsync(
        BlocwerkDbContext db, ICaptureFileStore files, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var row = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModel.WallId == wallId && t.Sha256 == sha)
            .Select(t => new { t.Id, t.StoredPath, t.ContentType, t.SizeBytes })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : File(AssetKind.Texture, row.Id, sha, row.ContentType, row.SizeBytes, files.ResolvePhysicalPath(row.StoredPath));
    }

    public static async Task<ResolvedAsset?> MaskAsync(
        BlocwerkDbContext db, ICaptureFileStore files, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var row = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModel.WallId == wallId && t.MaskStoredPath != null && t.MaskSha256 == sha)
            .Select(t => new { t.Id, t.MaskStoredPath, t.MaskSizeBytes })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : File(AssetKind.Mask, row.Id, sha, "image/png", row.MaskSizeBytes ?? 0, files.ResolvePhysicalPath(row.MaskStoredPath!));
    }

    /// <summary>
    /// A splat file: the full scene, the legacy mobile copy, or a ladder level. Ladder hashes are lowercase hex inside
    /// <c>LodLevelsJson</c>, so the rows are narrowed by a text match first and the level is then picked exactly.
    /// </summary>
    public static async Task<ResolvedAsset?> SplatAsync(
        BlocwerkDbContext db, ICaptureFileStore files, Guid wallId, byte[] sha, CancellationToken ct)
    {
        var hex = ContentHash.ToHex(sha)!;
        var rows = await db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.GeometryModel.WallId == wallId
                && (s.Sha256 == sha || s.MobileSha256 == sha || (s.LodLevelsJson != null && s.LodLevelsJson.Contains(hex))))
            .Select(s => new
            {
                s.Id, s.StoredPath, s.SizeBytes, s.Sha256, s.MobileStoredPath, s.MobileSizeBytes, s.MobileSha256, s.LodLevelsJson,
            })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var (stored, size) = PickSplatFile(
                sha, hex, (row.StoredPath, row.SizeBytes, row.Sha256), (row.MobileStoredPath, row.MobileSizeBytes ?? 0, row.MobileSha256), row.LodLevelsJson);
            if (stored is not null)
            {
                return File(AssetKind.Splat, row.Id, sha, SplatType, size, files.ResolvePhysicalPath(stored));
            }
        }

        return null;
    }

    private static (string? Stored, long Size) PickSplatFile(
        byte[] sha, string hex, (string Stored, long Size, byte[]? Sha) full, (string? Stored, long Size, byte[]? Sha) mobile, string? ladder)
    {
        if (ContentHash.Same(full.Sha, sha))
        {
            return (full.Stored, full.Size);
        }

        if (mobile.Stored is not null && ContentHash.Same(mobile.Sha, sha))
        {
            return (mobile.Stored, mobile.Size);
        }

        var level = SplatLodLadder.Parse(ladder).FirstOrDefault(l => string.Equals(l.Sha, hex, StringComparison.OrdinalIgnoreCase));
        return level is null ? (null, 0) : (level.StoredPath, level.SizeBytes);
    }

    private static ResolvedAsset? File(AssetKind kind, Guid id, byte[] sha, string type, long length, string? path) =>
        path is null ? null : new ResolvedAsset(kind, id, sha, type, length, path);
}
