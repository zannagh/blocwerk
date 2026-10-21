// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets;

/// <summary>
/// <see cref="IAssetHashReader"/> over the hash columns. Metadata projections only (never the bytes); the wall query
/// filter is bypassed because a hash grants nothing (the route gates), except for the generation photo, which goes
/// through the same gated tag read as its route.
/// </summary>
public sealed class AssetHashReader(IDbContextFactory<BlocwerkDbContext> dbContextFactory, IWallService wallService) : IAssetHashReader
{
    /// <inheritdoc/>
    public async Task<string?> WallPhotoAsync(Guid wallId, CancellationToken ct = default) =>
        (await WallPhotosAsync([wallId], ct)).GetValueOrDefault(wallId);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, string>> WallPhotosAsync(IReadOnlyCollection<Guid> wallIds, CancellationToken ct = default)
    {
        if (wallIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var centres = await db.WallPanels.AsNoTracking()
            .Where(p => wallIds.Contains(p.WallId) && p.Col == 0 && p.Row == 0 && p.Photo != null)
            .Select(p => new { p.WallId, p.Generation, p.PhotoSha256 })
            .ToListAsync(ct);
        var photos = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => wallIds.Contains(w.Id) && w.Photo != null && w.PhotoSha256 != null)
            .Select(w => new { w.Id, w.PhotoSha256 })
            .ToListAsync(ct);

        // The route serves the newest live centre row ((WallId, Col, Row, Generation) is unique, so there is one). An
        // unhashed newest row gives no hash at all: never an older row's, which would be other bytes.
        var result = new Dictionary<Guid, string>();
        var withCentre = centres.Select(c => c.WallId).ToHashSet();
        foreach (var wall in centres.GroupBy(c => c.WallId))
        {
            if (ContentHash.ToHex(wall.MaxBy(c => c.Generation)!.PhotoSha256) is { } sha)
            {
                result[wall.Key] = sha;
            }
        }

        // The retiring Wall.Photo is what /photo falls back to only when no live centre panel exists.
        foreach (var photo in photos.Where(p => !withCentre.Contains(p.Id)))
        {
            result[photo.Id] = ContentHash.ToHex(photo.PhotoSha256)!;
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<string?> WallPhotoAtGenerationAsync(Guid wallId, string? shareToken, int generation) =>
        (await wallService.GetPhotoTagForGenerationAsync(wallId, shareToken, generation))?.Sha;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<Guid, string>> PanelPhotosAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.WallPanels.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null && p.PhotoSha256 != null)
            .Select(p => new { p.Id, p.PhotoSha256 })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => ContentHash.ToHex(r.PhotoSha256)!);
    }
}
