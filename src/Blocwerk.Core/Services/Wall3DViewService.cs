// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Builds the 3D wall view. Visibility is NOT re-derived here: the wall is read through
/// <see cref="IWallService"/> exactly as the wall-detail page reads it (by id for members and the
/// kiosk on its own wall, by share token otherwise), so the membership query filter, the kiosk stamp
/// and the share-token lookup stay the single source of truth. Only after that read succeeded is the
/// wall's active geometry model loaded, with its textures (through <see cref="IWallCaptureService"/>,
/// which applies the same view rules and carries the share token into the URLs) and its photo-real
/// splat, if any.
/// </summary>
public sealed class Wall3DViewService(
    IWallService wallService,
    ICurrentUserService currentUserService,
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    IWallCaptureService captures,
    ILogger<Wall3DViewService> logger,
    ICaptureFileStore? files = null) : IWall3DViewService
{
    /// <inheritdoc />
    public async Task<Wall3DViewResult> BuildAsync(Guid wallId, Guid? boulderId, string? shareToken = null, CancellationToken ct = default)
    {
        var wall = await LoadVisibleWallAsync(wallId, shareToken);
        if (wall is null)
        {
            return new Wall3DViewResult(Wall3DViewStatus.NotFound);
        }

        // Update mode hides the wall from everyone except the admin who switched it on (WallDetail).
        if (wall.UnderMaintenance && await CurrentUserIdOrEmptyAsync() != wall.MaintenanceByUserId)
        {
            return new Wall3DViewResult(Wall3DViewStatus.UnderMaintenance, wall.Name);
        }

        var json = await LoadActiveGeometryJsonAsync(wall.Id, ct);
        if (json is null)
        {
            return new Wall3DViewResult(Wall3DViewStatus.NoGeometry, wall.Name);
        }

        WallGeometryDocument doc;
        try
        {
            doc = WallGeometryDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Active geometry model of wall {WallId} does not parse", wall.Id);
            return new Wall3DViewResult(Wall3DViewStatus.InvalidGeometry, wall.Name);
        }

        if (doc.Version != 1)
        {
            logger.LogWarning("Active geometry model of wall {WallId} has unknown schema version {Version}", wall.Id, doc.Version);
            return new Wall3DViewResult(Wall3DViewStatus.InvalidGeometry, wall.Name);
        }

        Dictionary<Wall3DPhotoKey, Wall3DPhotoMarkers> photoMarkers;
        List<HoldLinkPair> holdLinks;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            photoMarkers = await Wall3DPhotoMarkerLoader.LoadAsync(db, wall.Id, ct);

            // Overlapping panels each store their own copy of a hold; these links say which copies are one.
            holdLinks = await db.HoldLinks
                .AsNoTracking()
                .Where(l => l.WallId == wall.Id && l.Kind == HoldLinkKind.Same)
                .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
                .ToListAsync(ct);
        }

        var view = Wall3DViewBuilder.Build(wall, doc, boulderId, photoMarkers, holdLinks);
        view = await WithImageryAsync(view, shareToken, ct);
        return new Wall3DViewResult(Wall3DViewStatus.Ok, wall.Name, await WithPhotoOutlinesAsync(wall, view, doc, json, ct));
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveGeometryAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.WallGeometryModels.AnyAsync(m => m.WallId == wallId && m.IsActive, ct);
    }

    /// <summary>
    /// The wall as the wall page would load it, or null when this viewer may not see it. A share
    /// token must also belong to <paramref name="wallId"/>, so a token cannot be replayed on
    /// another wall's URL.
    /// </summary>
    private async Task<Wall?> LoadVisibleWallAsync(Guid wallId, string? shareToken)
    {
        if (!string.IsNullOrEmpty(shareToken))
        {
            var shared = await wallService.GetWallByShareTokenAsync(shareToken);
            return shared?.Id == wallId ? shared : null;
        }

        // Throws UnauthorizedAccessException for an anonymous caller that is not the kiosk on this
        // wall; the page turns that into the sign-in redirect, as WallDetail does.
        return await wallService.GetWallAsync(wallId);
    }

    private async Task<Guid> CurrentUserIdOrEmptyAsync()
    {
        try
        {
            return (await currentUserService.GetCurrentUserAsync()).Id;
        }
        catch (UnauthorizedAccessException)
        {
            return Guid.Empty;
        }
    }

    /// <summary>The active model's facet textures and photo-real splat, both authorized for this viewer.</summary>
    private async Task<Wall3DView> WithImageryAsync(Wall3DView view, string? shareToken, CancellationToken ct)
    {
        var textures = (await captures.GetActiveTexturesAsync(view.WallId, shareToken))
            .Select(t => new Wall3DTexture(t.FacetId, t.Url, new PlaneRectMm(t.AMin, t.AMax, t.BMin, t.BMax), t.MaskUrl))
            .ToList();

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var splat = await WallGeometrySplats.FindActiveAsync(db, view.WallId, ct);
        var matrix = splat is null ? null : CaptureSplatDocuments.WorldMatrix(splat.FrameJson);
        return view with
        {
            Textures = textures,
            SplatUrl = matrix is null ? null : WallGeometrySplats.Url(view.WallId, splat!.GeometryModelId, shareToken),
            SplatMobileUrl = matrix is null || splat!.MobileStoredPath is null
                ? null
                : WallGeometrySplats.Url(view.WallId, splat.GeometryModelId, shareToken, mobile: true),
            SplatLevels = matrix is null ? [] : SplatLevels(view.WallId, splat!, shareToken),
            SplatMatrix = matrix,
        };
    }

    /// <summary>The ladder, smallest first, then the full scene; rows without a ladder offer the legacy mobile copy.</summary>
    private static List<Wall3DSplatLevel> SplatLevels(Guid wallId, WallGeometrySplat splat, string? shareToken)
    {
        var modelId = splat.GeometryModelId;
        var levels = SplatLodLadder.Parse(splat.LodLevelsJson)
            .Select(l => new Wall3DSplatLevel(WallGeometrySplats.LevelUrl(wallId, modelId, l.Splats, shareToken), l.Splats, l.SizeBytes))
            .ToList();
        if (levels.Count == 0 && splat.MobileStoredPath is not null)
        {
            levels.Add(new Wall3DSplatLevel(
                WallGeometrySplats.Url(wallId, modelId, shareToken, mobile: true), SpzDecimator.MobileTarget, splat.MobileSizeBytes ?? 0));
        }

        levels.Add(new Wall3DSplatLevel(WallGeometrySplats.Url(wallId, modelId, shareToken), splat.SplatCount ?? 0, splat.SizeBytes));
        return levels;
    }

    /// <summary>
    /// Each hold's outline where the facet photo shows it (<see cref="Wall3DPhotoOutlines"/>), from the
    /// textures' source-view maps. Only once the textures themselves were authorized for this viewer.
    /// </summary>
    private async Task<Wall3DView> WithPhotoOutlinesAsync(Wall wall, Wall3DView view, WallGeometryDocument doc, string json, CancellationToken ct)
    {
        if (files is null || view.Textures.Count == 0)
        {
            return view;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var stored = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModel.WallId == view.WallId && t.GeometryModel.IsActive && t.SourceMapStoredPath != null)
            .Select(t => new { t.FacetId, Path = t.SourceMapStoredPath! })
            .ToListAsync(ct);
        var maps = new Dictionary<string, TextureSourceMap>(StringComparer.Ordinal);
        foreach (var t in stored)
        {
            if (await files.ReadAsync(t.Path, ct) is { } bytes && TextureSourceMap.Parse(bytes) is { } map)
            {
                maps[t.FacetId] = map;
            }
        }

        if (maps.Count == 0)
        {
            return view;
        }

        var perHold = await PanelCameraPerHoldAsync(db, wall, doc, ct);
        return Wall3DPhotoOutlines.Apply(view, doc, maps, SolvedCamera.ParseAll(json), perHold);
    }

    /// <summary>
    /// Each live hold's panel-photo camera centre (<see cref="PanelCameraEstimator"/>): resected from the placed
    /// holds of that photo, or a planar pose from the photo's size and EXIF focal length when they are coplanar.
    /// Loaded once per view, reading only each photo's header.
    /// </summary>
    private static async Task<Dictionary<Guid, double[]>> PanelCameraPerHoldAsync(
        BlocwerkDbContext db, Wall wall, WallGeometryDocument doc, CancellationToken ct)
    {
        var frames = doc.Segments.SelectMany(s => s.Facets)
            .Where(f => !string.IsNullOrEmpty(f.Id))
            .Select(f => (f.Id, Frame: FacetFrame.From(f)))
            .Where(x => x.Frame is not null)
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First().Frame!, StringComparer.Ordinal);
        var live = wall.Holds.Where(h => h.Generation <= wall.CurrentGeneration).ToList();
        var placed = live.Where(h => h.FacetId is { } f && frames.ContainsKey(f) && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue);
        var photos = await PanelPhotoInfoLoader.LoadAsync(db, wall.Id, placed.Select(HoldPlaneProjector.PhotoOf), ct);
        var cameras = HoldFootprintRefiner.PanelCameras(live, frames, photos);
        return live
            .Where(h => cameras.ContainsKey(HoldPlaneProjector.PhotoOf(h)))
            .ToDictionary(h => h.Id, h => cameras[HoldPlaneProjector.PhotoOf(h)]);
    }

    private async Task<string?> LoadActiveGeometryJsonAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.WallGeometryModels
            .AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => m.Json)
            .FirstOrDefaultAsync(ct);
    }
}
