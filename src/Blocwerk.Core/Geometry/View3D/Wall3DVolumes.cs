// <copyright file="Wall3DVolumes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Adds the detected volumes to a built <see cref="Wall3DView"/> and moves the holds placed on them
/// (<see cref="Hold.VolumePlacementJson"/>, current for the hold's flat position and on a visible volume) onto
/// the volume: their protrusion gets the volume surface as base, the move as shift and the surface normal. Such a
/// hold's <see cref="Wall3DHold.PhotoOutline"/> is dropped: the textured volume shows it where it really is.
/// A view without volumes is returned unchanged.
/// </summary>
public static class Wall3DVolumes
{
    private const double MinBodyMm = 8;

    /// <summary>The view with volumes.</summary>
    /// <param name="view">The built view.</param>
    /// <param name="volumes">The active model's visible volumes.</param>
    /// <param name="holds">The wall's holds (their stored placements).</param>
    /// <param name="sourceMaps">The facet textures' source maps, by facet (optional).</param>
    /// <param name="cameras">The model's solved cameras (for the texture camera).</param>
    /// <returns>The view.</returns>
    public static Wall3DView Apply(
        Wall3DView view,
        IReadOnlyList<WallVolume> volumes,
        IEnumerable<Hold> holds,
        IReadOnlyDictionary<string, TextureSourceMap> sourceMaps,
        IReadOnlyList<SolvedCamera> cameras)
    {
        var frames = view.Facets.ToDictionary(f => f.Id, f => new FacetFrame(f.Origin, f.U, f.V, f.Normal), StringComparer.Ordinal);
        var drawn = new List<Wall3DVolume>();
        foreach (var v in volumes.OrderBy(v => v.Index))
        {
            if (!frames.TryGetValue(v.FacetId, out var frame) || VolumeSurface.FromJson(v.SurfaceJson) is not { } surface)
            {
                continue;
            }

            var footprint = Footprint(v.FootprintJson);
            var g = surface.Grid;
            drawn.Add(new Wall3DVolume(
                v.Id, v.Index, v.FacetId, g.ALo, g.BLo, g.CellMm, g.Cols, g.Rows, surface.Heights, footprint.Select(p => new[] { p.A, p.B }).ToList(),
                TextureCamera(frame, footprint, sourceMaps.GetValueOrDefault(v.FacetId), cameras)));
        }

        if (drawn.Count == 0)
        {
            return view;
        }

        var visible = drawn.Select(v => v.Id).ToHashSet();
        var placements = holds
            .Select(h => (h.Id, P: HoldVolumePlacement.FromJson(h.VolumePlacementJson), h.PlaneAMm, h.PlaneBMm))
            .Where(x => x.P is not null && visible.Contains(x.P.VolumeId) && x.P.Matches(x.PlaneAMm, x.PlaneBMm))
            .ToDictionary(x => x.Id, x => x.P!);
        var moved = view.Holds.Select(h => placements.TryGetValue(h.Id, out var p) ? OnVolume(h, p) : h).ToList();
        return view with { Volumes = drawn, Holds = moved };
    }

    /// <summary>A stored footprint as (a, b) pairs; empty when malformed.</summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The ring.</returns>
    public static List<(double A, double B)> Footprint(string json)
    {
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<double[][]>(json) ?? [])
                .Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static Wall3DHold OnVolume(Wall3DHold h, HoldVolumePlacement p)
    {
        // The hold's own relief above whatever it was measured on (the wall, or the splat's volume surface).
        var old = h.Protrusion;
        var body = Math.Max(MinBodyMm, old is null ? 20 : old.HeightMm - old.BaseMm);
        var apex = old is null ? body * 1.3 : Math.Max(body, old.ApexMm - old.BaseMm);
        var shiftA = Math.Round(p.A - h.PlaneA, 1);
        var shiftB = Math.Round(p.B - h.PlaneB, 1);
        var protrusion = new Wall3DHoldProtrusion(
            p.H, Math.Round(p.H + body, 1), shiftA, shiftB, Math.Round(p.H + apex, 1), old?.Measured ?? false, true, shiftA, shiftB, p.Normal, p.VolumeId);
        return h with { Protrusion = protrusion, PhotoOutline = null };
    }

    private static double[]? TextureCamera(
        FacetFrame frame, List<(double A, double B)> footprint, TextureSourceMap? map, IReadOnlyList<SolvedCamera> cameras)
    {
        if (map is null || footprint.Count < 3)
        {
            return null;
        }

        var samples = new List<(double A, double B)>();
        double a0 = footprint.Min(p => p.A), a1 = footprint.Max(p => p.A), b0 = footprint.Min(p => p.B), b1 = footprint.Max(p => p.B);
        for (var a = a0; a <= a1; a += map.CellMm)
        {
            for (var b = b0; b <= b1; b += map.CellMm)
            {
                if (PlanePolygon.Contains(footprint, (a, b)))
                {
                    samples.Add((a, b));
                }
            }
        }

        var name = map.Dominant(samples);
        var camera = name is null ? null : cameras.FirstOrDefault(c => string.Equals(c.Image, name, StringComparison.OrdinalIgnoreCase));
        if (camera is null)
        {
            return null;
        }

        var c = camera.Centre;
        var (la, lb, lh) = FacetCloud.Local(frame, c[0], c[1], c[2]);
        return lh > 0 ? [Math.Round(la, 1), Math.Round(lb, 1), Math.Round(lh, 1)] : null;
    }
}
