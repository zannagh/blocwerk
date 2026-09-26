// <copyright file="WallVolumeShapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Services;

/// <summary>
/// Switches a stored <see cref="WallVolume"/> between its measured height field and flat faces
/// (<see cref="FlatSidedFitter"/>). The flat shape replaces <see cref="WallVolume.SurfaceJson"/> (format version 2), so
/// everything that reads the surface (hold placement, footprints, coverage, the 3D view) uses the faces without
/// knowing; the height field waits in <see cref="WallVolume.HeightFieldJson"/> and comes back when switched off.
/// </summary>
public static class WallVolumeShapes
{
    /// <summary>
    /// Switches flat sides on or off. On: fitted afresh from the measured height field, and applied when the fit is good
    /// or <paramref name="force"/> (an admin asked for it: ticked on the volume or applied to all; the fit error is then
    /// only shown); otherwise the volume is left as it was. Returns the fit
    /// (null when off or impossible); whether it was applied is <see cref="WallVolume.HasFlatSides"/>.
    /// </summary>
    /// <param name="volume">The volume (changed in place).</param>
    /// <param name="on">Flat sides on.</param>
    /// <param name="force">Apply a poor fit too.</param>
    /// <returns>The fit or null.</returns>
    public static FlatSidedFit? SetFlatSides(WallVolume volume, bool on, bool force)
    {
        if (!on)
        {
            if (volume.HeightFieldJson is { } measured)
            {
                volume.SurfaceJson = measured;
            }

            (volume.HeightFieldJson, volume.HasFlatSides, volume.FlatFitRmsMm) = (null, false, null);
            return null;
        }

        var fieldJson = volume.HeightFieldJson ?? volume.SurfaceJson;
        if (VolumeSurface.FromJson(fieldJson) is not { Polyhedron: null } field
            || FlatSidedFitter.Fit(field, Wall3DVolumes.Footprint(volume.FootprintJson)) is not { } fit)
        {
            return null;
        }

        if (fit.IsGood || force)
        {
            volume.HeightFieldJson = fieldJson;
            volume.SurfaceJson = VolumeSurface.FlatSided(fit.Polyhedron, field.Grid.CellMm).ToJson();
            (volume.HasFlatSides, volume.FlatFitRmsMm) = (true, fit.RmsMm);
        }

        return fit;
    }

    /// <summary>The admin list's view of a volume.</summary>
    /// <param name="v">The volume.</param>
    /// <returns>The summary.</returns>
    public static WallVolumeSummary Summary(WallVolume v)
    {
        var polyhedron = v.HasFlatSides ? VolumeSurface.FromJson(v.SurfaceJson)?.Polyhedron : null;
        var ring = Wall3DVolumes.Footprint(v.FootprintJson);
        return new WallVolumeSummary(
            v.Id,
            v.Index,
            v.FacetId,
            v.AreaM2,
            v.HeightMm,
            v.Confidence,
            v.HoldCount,
            v.IsHidden,
            v.IsRemoved,
            polyhedron is not null,
            polyhedron?.Shape,
            polyhedron?.SideCount ?? 0,
            polyhedron is null ? null : v.FlatFitRmsMm,
            ring.Count == 0 ? 0 : Math.Round(ring.Average(p => p.A)),
            ring.Count == 0 ? 0 : Math.Round(ring.Average(p => p.B)));
    }
}
