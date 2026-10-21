// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// Builds a wall's manifest for one <see cref="ManifestAudience"/> from the hash columns, always from the database and
/// never from a circuit's cached wall (a cached wall can hold the null hashes it was loaded with before the backfill).
/// The caller has passed <see cref="Resolution.AssetAccessGate"/> already; the build itself depends on the wall and the
/// class only, which is what lets one build serve every caller of the class.
/// </summary>
/// <remarks>
/// Rows whose hash is still null are listed with <c>sha: null</c> (and counted in <see cref="WallManifest.Pending"/>):
/// the bytes exist but have no hashed URL yet, so a client keeps them network-only. They are never hashed here, on a
/// request path a share link can reach; the backfill hashes them and announces the wall (<see cref="ManifestBackfillAnnouncer"/>).
/// </remarks>
public sealed class WallManifestBuilder(RootDbContextFactory dbContextFactory, IWallImageStorage imageStorage, ICaptureFileStore captureFiles)
{
    /// <summary>The manifest of <paramref name="wallId"/> for <paramref name="audience"/>, or null when the wall is gone.</summary>
    public async Task<WallManifest?> BuildAsync(Guid wallId, ManifestAudience audience, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var generation = await db.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => (int?)w.CurrentGeneration)
            .FirstOrDefaultAsync(ct);
        if (generation is null)
        {
            return null;
        }

        var (panels, originCovered) = await ManifestPhotoEntries.PanelsAsync(db, wallId, ct);
        var entries = new List<ManifestAsset>(panels);
        entries.AddRange(await ManifestPhotoEntries.WallPhotoAsync(db, wallId, originCovered, ct));
        entries.AddRange(await ManifestPhotoEntries.ResetsAsync(db, wallId, ct));
        entries.AddRange(await ManifestPhotoEntries.GalleryAsync(db, imageStorage, wallId, ct));
        entries.AddRange(await ManifestGeometryEntries.TexturesAsync(db, captureFiles, wallId, ct));
        entries.AddRange(await ManifestGeometryEntries.SplatsAsync(db, captureFiles, wallId, ct));
        entries.AddRange(await ManifestVideoEntries.ThumbnailsAsync(db, wallId, audience, ct));
        return ManifestAssembly.Assemble(wallId, generation.Value, entries);
    }
}
