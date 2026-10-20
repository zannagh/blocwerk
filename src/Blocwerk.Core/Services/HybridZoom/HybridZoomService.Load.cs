// <copyright file="HybridZoomService.Load.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>The reads behind a close-up: the panel stamp, the active model, its capture photos and the panel's mapping.</summary>
public sealed partial class HybridZoomService
{
    private const int MaxLineageDepth = 50;
    private const int MaxParsedModels = 8;

    private static readonly ConcurrentDictionary<Guid, ZoomModel?> ParsedModels = new();

    private static async Task<ZoomPanel?> LoadPanelAsync(BlocwerkDbContext db, Guid wallId, Guid panelId, CancellationToken ct) =>
        await db.WallPanels.AsNoTracking()
            .Where(p => p.Id == panelId && p.WallId == wallId && p.Photo != null)
            .Select(p => new ZoomPanel(p.Id, p.Generation, p.PhotoRevision, p.CreatedAt))
            .FirstOrDefaultAsync(ct);

    private static async Task<Guid?> ActiveModelIdAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Everything the plan needs, or why there is nothing to plan from: no model, photos or panel size
    /// (<see cref="CloseUpStatus.NoModel"/>), or a panel photo taken clearly after the capture
    /// (<see cref="CloseUpStatus.StaleCapture"/>: the capture shows an older wall). The capture's time is its last photo's
    /// upload; <paramref name="freshness"/> is how much later a panel photo may still show the same wall (null: no check).
    /// </summary>
    private static async Task<(ZoomInputs? Inputs, CloseUpStatus Status)> LoadInputsAsync(
        BlocwerkDbContext db, Guid wallId, ZoomPanel panel, Guid modelId, TimeSpan? freshness, CancellationToken ct)
    {
        var model = await ModelAsync(db, modelId, ct);
        var (photos, capturedAt) = model is null ? ([], null) : await CapturePhotosAsync(db, wallId, modelId, ct);
        if (model is null || photos.Count == 0)
        {
            return (null, CloseUpStatus.NoModel);
        }

        if (capturedAt is { } at && freshness is { } window && panel.CreatedAt > at + window)
        {
            return (null, CloseUpStatus.StaleCapture);
        }

        var live = await LiveWallHolds.QueryAsync(db, wallId, ct);
        var placed = await live.AsNoTracking()
            .Where(h => h.WallPanelId == panel.Id && h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null)
            .Select(h => new Hold
            {
                Id = h.Id, WallId = h.WallId, WallPanelId = h.WallPanelId, Generation = h.Generation, X = h.X, Y = h.Y,
                FacetId = h.FacetId, PlaneAMm = h.PlaneAMm, PlaneBMm = h.PlaneBMm,
            })
            .ToListAsync(ct);
        var markers = await Wall3DPhotoMarkerLoader.LoadAsync(db, wallId, ct);
        var generation = placed.GroupBy(h => h.Generation).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault()
                         ?? panel.Generation;
        var key = new Wall3DPhotoKey(panel.Id, generation);
        var info = (await PanelPhotoInfoLoader.LoadAsync(db, wallId, [key], ct)).GetValueOrDefault(key);
        if (info is null)
        {
            return (null, CloseUpStatus.NoModel);
        }

        var projector = HoldPlaneProjector.Create(placed.Where(h => model.Frames.ContainsKey(h.FacetId!)), model.Document, markers);
        var volumes = await VolumesAsync(db, modelId, ct);
        return (new ZoomInputs(model, photos, projector, generation, info, volumes), CloseUpStatus.Rendered);
    }

    /// <summary>The model's frames and cameras, parsed once per model id; null when it has no usable cameras.</summary>
    private static async Task<ZoomModel?> ModelAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        if (ParsedModels.TryGetValue(modelId, out var cached))
        {
            return cached;
        }

        var json = await db.WallGeometryModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Json).FirstOrDefaultAsync(ct);
        var parsed = json is null ? null : Parse(json);
        if (ParsedModels.Count >= MaxParsedModels)
        {
            ParsedModels.Clear();
        }

        ParsedModels[modelId] = parsed;
        return parsed;
    }

    private static ZoomModel? Parse(string json)
    {
        WallGeometryDocument doc;
        try
        {
            doc = WallGeometryDocument.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        var cameras = SolvedCamera.ParseAll(json)
            .Where(c => c.Width > 0 && c.Height > 0)
            .GroupBy(c => c.Image, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var frames = doc.Segments.SelectMany(s => s.Facets)
            .Where(f => !string.IsNullOrEmpty(f.Id))
            .Select(f => (f.Id, Frame: FacetFrame.From(f)))
            .Where(x => x.Frame is not null)
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Frame!, StringComparer.Ordinal);
        var extents = Wall3DFallbackPlacement.FacetExtents(doc);
        return cameras.Count == 0 || frames.Count == 0
            ? null
            : new ZoomModel(doc, frames, extents, cameras, CastFacets(doc, frames, extents));
    }

    /// <summary>The facets with an extent and their real outlines (no volumes: those are cut out on their own).</summary>
    private static List<CastFacet> CastFacets(
        WallGeometryDocument doc, IReadOnlyDictionary<string, FacetFrame> frames, IReadOnlyDictionary<string, PlaneRectMm> extents)
    {
        var outlines = FacetShapes.Outlines(doc, null);
        return frames
            .Where(f => extents.ContainsKey(f.Key))
            .Select(f => new CastFacet(f.Key, f.Value, extents[f.Key], [], outlines.GetValueOrDefault(f.Key)))
            .ToList();
    }

    /// <summary>
    /// The retained, non-blurry photos of the capture behind the model, by camera name, and when that capture was
    /// made: the model's own capture, else (for a correction) the capture of the model it was derived from, and so
    /// on up the lineage.
    /// </summary>
    private static async Task<(Dictionary<string, ZoomSourcePhoto> Photos, DateTimeOffset? CapturedAt)> CapturePhotosAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct)
    {
        var lineage = await db.WallGeometryModels.AsNoTracking().Where(m => m.WallId == wallId)
            .Select(m => new { m.Id, m.DerivedFromModelId }).ToDictionaryAsync(m => m.Id, m => m.DerivedFromModelId, ct);
        Guid? current = modelId;
        for (var depth = 0; depth < MaxLineageDepth && current is { } id; depth++)
        {
            var rows = await db.WallCapturePhotos.AsNoTracking()
                .Where(p => p.Capture.GeometryModelId == id && !p.ExcludedBlurry)
                .Select(p => new { p.Index, p.OriginalFileName, p.StoredPath, p.Width, p.Height, p.Sharpness, p.UploadedAt })
                .ToListAsync(ct);
            if (rows.Count > 0)
            {
                var byPath = rows.GroupBy(r => r.StoredPath).ToDictionary(g => g.Key, g => g.First());
                var photos = CameraPhotoNames.Map(rows.Select(r => (r.Index, r.OriginalFileName, r.StoredPath)))
                    .ToDictionary(
                        e => e.Key,
                        e => new ZoomSourcePhoto(e.Value, byPath[e.Value].Width, byPath[e.Value].Height, byPath[e.Value].Sharpness),
                        StringComparer.OrdinalIgnoreCase);
                return (photos, rows.Max(r => r.UploadedAt));
            }

            current = lineage.GetValueOrDefault(id);
        }

        return ([], null);
    }

    private static async Task<Dictionary<string, List<VolumeOutline>>> VolumesAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        var rows = await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == modelId && !v.IsHidden && !v.IsRemoved)
            .Select(v => new { v.Id, v.FacetId, v.FootprintJson, v.HeightMm })
            .ToListAsync(ct);
        return rows
            .Select(r => (r.FacetId, Outline: new VolumeOutline(r.Id, Wall3DVolumes.Footprint(r.FootprintJson), r.HeightMm)))
            .Where(x => x.Outline.Outline.Count >= 3)
            .GroupBy(x => x.FacetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Outline).ToList(), StringComparer.Ordinal);
    }
}
