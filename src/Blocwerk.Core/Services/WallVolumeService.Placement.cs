// <copyright file="WallVolumeService.Placement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>Placing the live holds onto the model's visible volumes.</summary>
public sealed partial class WallVolumeService
{
    /// <summary>
    /// (Re)places every live placed hold: a hold whose panel ray meets a visible volume gets its placement, any
    /// other loses a stored one. Also stores each volume's hold count. Returns (holds on volumes, holds changed).
    /// </summary>
    internal static Task<(int Placed, int Changed)> PlaceHoldsAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, CancellationToken ct) =>
        PlaceHoldsAsync(db, wallId, modelId, null, ct);

    /// <summary>As <see cref="PlaceHoldsAsync(BlocwerkDbContext, Guid, Guid, CancellationToken)"/>, collecting the ids of the holds that changed.</summary>
    internal static async Task<(int Placed, int Changed)> PlaceHoldsAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, ICollection<Guid>? changedIds, CancellationToken ct)
    {
        var json = await db.WallGeometryModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Json).FirstAsync(ct);
        var (frames, _) = FacetsOf(WallGeometryDocument.Parse(json));
        var rows = await db.WallVolumes.Where(v => v.GeometryModelId == modelId).ToListAsync(ct);
        var volumes = rows.Where(v => !v.IsHidden && !v.IsRemoved)
            .Select(v => VolumeSurface.FromJson(v.SurfaceJson) is { } s ? new PlacedVolume(v.Id, v.FacetId, s) : null)
            .OfType<PlacedVolume>()
            .GroupBy(v => v.FacetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PlacedVolume>)g.ToList(), StringComparer.Ordinal);
        var liveIds = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).Select(h => h.Id).ToListAsync(ct);
        var holds = await db.Holds.Where(h => liveIds.Contains(h.Id)).ToListAsync(ct);
        var placedHolds = holds.Where(h => h.FacetId is { } f && frames.ContainsKey(f) && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue).ToList();
        var photos = await PanelPhotoInfoLoader.LoadAsync(db, wallId, placedHolds.Select(HoldPlaneProjector.PhotoOf), ct);

        // The flat positions of holds on or near a volume are off by up to ~200 mm: they would bias the resection.
        var panelCams = HoldFootprintRefiner.PanelCameras(placedHolds.Where(h => !NearVolume(h, volumes)).ToList(), frames, photos);
        var captureCams = SolvedCamera.ParseAll(json).Select(c => c.Centre).ToList();
        var minHeight = new VolumeDetectionOptions().MinPlacementHeightMm;
        int placed = 0, changed = 0;
        var perVolume = new Dictionary<Guid, int>();
        foreach (var hold in holds)
        {
            var placement = PlacementOf(hold, frames, volumes, panelCams, captureCams, minHeight);
            var value = placement?.ToJson();
            if (placement is not null)
            {
                placed++;
                perVolume[placement.VolumeId] = perVolume.GetValueOrDefault(placement.VolumeId) + 1;
            }

            if (hold.VolumePlacementJson != value)
            {
                hold.VolumePlacementJson = value;
                changed++;
                changedIds?.Add(hold.Id);
            }
        }

        foreach (var row in rows)
        {
            row.HoldCount = perVolume.GetValueOrDefault(row.Id);
        }

        await db.SaveChangesAsync(ct);
        return (placed, changed);
    }

    private static HoldVolumePlacement? PlacementOf(
        Hold hold,
        Dictionary<string, FacetFrame> frames,
        Dictionary<string, IReadOnlyList<PlacedVolume>> volumes,
        Dictionary<Wall3DPhotoKey, double[]> panelCams,
        List<double[]> captureCams,
        double minHeight)
    {
        if (hold.FacetId is not { } facetId || !frames.TryGetValue(facetId, out var frame) || hold.PlaneAMm is not { } a
            || hold.PlaneBMm is not { } b || !volumes.TryGetValue(facetId, out var onFacet))
        {
            return null;
        }

        var panel = panelCams.GetValueOrDefault(HoldPlaneProjector.PhotoOf(hold));
        return HoldVolumePlacer.Place(frame, a, b, onFacet, panel, captureCams, minHeight);
    }

    private static bool NearVolume(Hold hold, Dictionary<string, IReadOnlyList<PlacedVolume>> volumes)
    {
        const double marginMm = 250;
        if (!volumes.TryGetValue(hold.FacetId!, out var onFacet))
        {
            return false;
        }

        double a = hold.PlaneAMm!.Value, b = hold.PlaneBMm!.Value;
        return onFacet.Any(v => a > v.Surface.Grid.ALo - marginMm && b > v.Surface.Grid.BLo - marginMm
            && a < v.Surface.Grid.ALo + (v.Surface.Grid.Cols * v.Surface.Grid.CellMm) + marginMm
            && b < v.Surface.Grid.BLo + (v.Surface.Grid.Rows * v.Surface.Grid.CellMm) + marginMm);
    }
}
