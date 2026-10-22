// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// The photo kinds of a manifest: panels, the wall photo, reset photos and gallery images. Metadata only (hash, type and
/// a server-side length), read straight from the database, never from a circuit's cached wall.
/// </summary>
internal static class ManifestPhotoEntries
{
    private const string Jpeg = "image/jpeg";

    /// <summary>
    /// Every committed panel row. Per grid cell the newest generation (smallest id on a tie, as the panel list picks it)
    /// is <c>core</c>; the superseded rows a historic boulder is drawn on are <c>lazy</c>. Staged photos are never listed.
    /// </summary>
    public static async Task<(List<ManifestAsset> Assets, bool OriginCovered)> PanelsAsync(
        BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var rows = await db.WallPanels.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation, p.PhotoSha256, p.PhotoContentType, Length = (long)p.Photo!.Length })
            .ToListAsync(ct);
        var current = rows.GroupBy(p => (p.Col, p.Row))
            .Select(g => g.OrderByDescending(p => p.Generation).ThenBy(p => p.Id).First().Id)
            .ToHashSet();

        var assets = rows.Select(p => Photo(ManifestAssetKinds.Panel, p.Id, p.PhotoSha256, p.PhotoContentType, p.Length,
                current.Contains(p.Id) ? ManifestTiers.Core : ManifestTiers.Lazy) with { Col = p.Col, Row = p.Row, Gen = p.Generation })
            .ToList();
        return (assets, rows.Any(p => p.Col == 0 && p.Row == 0));
    }

    /// <summary>
    /// The retiring single-image photo: <c>core</c> when no committed panel covers the origin cell (the panel photo route
    /// falls back to it there), otherwise <c>lazy</c>.
    /// </summary>
    public static async Task<List<ManifestAsset>> WallPhotoAsync(BlocwerkDbContext db, Guid wallId, bool originCovered, CancellationToken ct)
    {
        var row = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId && w.Photo != null)
            .Select(w => new { w.PhotoSha256, w.PhotoContentType, Length = (long)w.Photo!.Length })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? []
            : [Photo(ManifestAssetKinds.WallPhoto, wallId, row.PhotoSha256, row.PhotoContentType, row.Length, originCovered ? ManifestTiers.Lazy : ManifestTiers.Core)];
    }

    public static async Task<List<ManifestAsset>> ResetsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var rows = await db.WallResets.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.WallId == wallId && r.PreviousPhoto != null)
            .Select(r => new { r.Id, r.Generation, r.PreviousPhotoSha256, r.PreviousPhotoContentType, Length = (long)r.PreviousPhoto!.Length })
            .ToListAsync(ct);
        return rows.Select(r => Photo(ManifestAssetKinds.ResetPhoto, r.Id, r.PreviousPhotoSha256, r.PreviousPhotoContentType, r.Length, ManifestTiers.Lazy)
            with { Gen = r.Generation }).ToList();
    }

    /// <summary>The gallery uploads; a stored name that escapes the store is left out, as the resolver refuses it.</summary>
    public static async Task<List<ManifestAsset>> GalleryAsync(BlocwerkDbContext db, IWallImageStorage storage, Guid wallId, CancellationToken ct)
    {
        var rows = await db.WallImages.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.WallId == wallId)
            .Select(i => new { i.Id, i.StoragePath, i.ContentType, i.SizeBytes, i.Sha256, i.CreatedAt })
            .ToListAsync(ct);
        return rows.Where(i => storage.ResolvePhysicalPath(i.StoragePath) is not null)
            .Select(i => Photo(ManifestAssetKinds.Gallery, i.Id, i.Sha256, i.ContentType, i.SizeBytes, ManifestTiers.Lazy)
                with { At = i.CreatedAt })
            .ToList();
    }

    private static ManifestAsset Photo(string kind, Guid id, byte[]? sha, string? type, long bytes, string tier) => new()
    {
        Sha = ContentHash.ToHex(sha),
        Kind = kind,
        Id = id,
        Type = type ?? Jpeg,
        Bytes = bytes,
        Widths = ImageVariants.Widths,
        Tier = tier,
    };
}
