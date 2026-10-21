// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// The 3D kinds of a manifest, of the wall's active geometry model only (what the 3D view draws): facet textures and
/// masks (<c>view3d</c>), and the splat levels the viewer offers (<c>optin</c>). A file whose stored name escapes the
/// capture store is left out, as the resolver refuses it.
/// </summary>
internal static class ManifestGeometryEntries
{
    private const string SplatType = "application/octet-stream";

    public static async Task<List<ManifestAsset>> TexturesAsync(BlocwerkDbContext db, ICaptureFileStore files, Guid wallId, CancellationToken ct)
    {
        var rows = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModel.WallId == wallId && t.GeometryModel.IsActive)
            .Select(t => new { t.Id, t.FacetId, t.StoredPath, t.ContentType, t.SizeBytes, t.Sha256, t.MaskStoredPath, t.MaskSizeBytes, t.MaskSha256 })
            .ToListAsync(ct);

        var assets = new List<ManifestAsset>();
        foreach (var t in rows)
        {
            if (files.ResolvePhysicalPath(t.StoredPath) is not null)
            {
                assets.Add(File(ManifestAssetKinds.Texture, t.Id, t.Sha256, t.ContentType, t.SizeBytes, ManifestTiers.View3d) with { Facet = t.FacetId });
            }

            if (t.MaskStoredPath is not null && files.ResolvePhysicalPath(t.MaskStoredPath) is not null)
            {
                assets.Add(File(ManifestAssetKinds.Mask, t.Id, t.MaskSha256, "image/png", t.MaskSizeBytes ?? 0, ManifestTiers.View3d) with { Facet = t.FacetId });
            }
        }

        return assets;
    }

    /// <summary>
    /// The levels the 3D view offers (<c>Wall3DViewService.SplatLevels</c>): the LOD ladder, or the legacy mobile copy
    /// when there is no ladder, then the full scene. A device keeps the one level it renders, by its splat count.
    /// </summary>
    public static async Task<List<ManifestAsset>> SplatsAsync(BlocwerkDbContext db, ICaptureFileStore files, Guid wallId, CancellationToken ct)
    {
        var splat = await WallGeometrySplats.FindActiveAsync(db, wallId, ct);
        if (splat is null)
        {
            return [];
        }

        var levels = SplatLodLadder.Parse(splat.LodLevelsJson)
            .Select(l => (l.StoredPath, l.SizeBytes, Sha: ContentHash.FromHex(l.Sha), l.Splats))
            .ToList();
        if (levels.Count == 0 && splat.MobileStoredPath is not null)
        {
            levels.Add((splat.MobileStoredPath, splat.MobileSizeBytes ?? 0, splat.MobileSha256, SpzDecimator.MobileTarget));
        }

        levels.Add((splat.StoredPath, splat.SizeBytes, splat.Sha256, splat.SplatCount ?? 0));
        return levels.Where(l => files.ResolvePhysicalPath(l.StoredPath) is not null)
            .Select(l => File(ManifestAssetKinds.Splat, splat.Id, l.Sha, SplatType, l.SizeBytes, ManifestTiers.Optin) with { Splats = l.Splats })
            .ToList();
    }

    private static ManifestAsset File(string kind, Guid id, byte[]? sha, string type, long bytes, string tier) => new()
    {
        Sha = ContentHash.ToHex(sha),
        Kind = kind,
        Id = id,
        Type = type,
        Bytes = bytes,
        Tier = tier,
    };
}
