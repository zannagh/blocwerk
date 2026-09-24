// <copyright file="HoldVolumePlacer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>A volume ready for placing holds: its id, facet and surface.</summary>
/// <param name="Id">The volume.</param>
/// <param name="FacetId">Its facet.</param>
/// <param name="Surface">Its shape.</param>
public sealed record PlacedVolume(Guid Id, string FacetId, VolumeSurface Surface);

/// <summary>
/// Moves a hold that sits on a volume onto the volume. Its flat facet position is where the PANEL photo's ray
/// through the hold meets the facet plane (the photo is registered onto the facet texture with one plane
/// homography); the hold itself is where that ray first meets a volume. The panel camera comes from the placed
/// holds (<see cref="Footprints.PanelCameraEstimator"/>); without one, the most frontal capture camera stands in
/// (the old <see cref="HoldVolumeRemap"/> approximation). On The Attic this took the volume holds' median error
/// against the multi-view evidence from 72 mm to 23 mm (2026-09-25).
/// </summary>
public static class HoldVolumePlacer
{
    private const double MinCameraOffsetMm = 300;

    /// <summary>The placement of one hold, or null when its ray meets no volume.</summary>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="a">Its flat position along u, mm.</param>
    /// <param name="b">Its flat position along v, mm.</param>
    /// <param name="volumes">The facet's volumes.</param>
    /// <param name="panelCamera">The panel photo's camera centre (world mm), when known.</param>
    /// <param name="captureCameras">The capture cameras' centres (world mm), the fallback.</param>
    /// <param name="minHeightMm">See <see cref="VolumeDetectionOptions.MinPlacementHeightMm"/>.</param>
    /// <returns>The placement or null.</returns>
    public static HoldVolumePlacement? Place(
        FacetFrame frame,
        double a,
        double b,
        IReadOnlyList<PlacedVolume> volumes,
        double[]? panelCamera,
        IReadOnlyList<double[]> captureCameras,
        double minHeightMm)
    {
        if (volumes.Count == 0)
        {
            return null;
        }

        var (camera, kind) = panelCamera is not null ? (panelCamera, "panel") : (Frontal(frame, a, b, captureCameras), "frontal");
        if (camera is null)
        {
            return null;
        }

        var from = FacetCloud.Local(frame, camera[0], camera[1], camera[2]);
        if (from.H < MinCameraOffsetMm)
        {
            return null;
        }

        (PlacedVolume Volume, (double A, double B, double H) Hit)? best = null;
        foreach (var v in volumes)
        {
            if (v.Surface.RayHit(from, a, b, minHeightMm) is { } hit && (best is null || hit.H > best.Value.Hit.H))
            {
                best = (v, hit);
            }
        }

        if (best is not { } found)
        {
            return null;
        }

        var (ha, hb, hh) = found.Hit;
        var normal = found.Volume.Surface.NormalAt(ha, hb).Select(x => Math.Round(x, 4)).ToArray();
        return new HoldVolumePlacement(found.Volume.Id, Math.Round(ha, 1), Math.Round(hb, 1), Math.Round(hh, 1), normal, a, b, kind);
    }

    /// <summary>A world point on the placement (facet frame → world).</summary>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="p">The placement.</param>
    /// <returns>World mm.</returns>
    public static double[] World(FacetFrame frame, HoldVolumePlacement p) => frame.ToWorld(p.A, p.B, p.H);

    /// <summary>The placement's surface normal in world coordinates.</summary>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="p">The placement.</param>
    /// <returns>A unit vector.</returns>
    public static double[] WorldNormal(FacetFrame frame, HoldVolumePlacement p) =>
    [
        (p.Normal[0] * frame.U[0]) + (p.Normal[1] * frame.V[0]) + (p.Normal[2] * frame.Normal[0]),
        (p.Normal[0] * frame.U[1]) + (p.Normal[1] * frame.V[1]) + (p.Normal[2] * frame.Normal[1]),
        (p.Normal[0] * frame.U[2]) + (p.Normal[1] * frame.V[2]) + (p.Normal[2] * frame.Normal[2]),
    ];

    private static double[]? Frontal(FacetFrame frame, double a, double b, IReadOnlyList<double[]> cameras)
    {
        double[]? best = null;
        var bestCos = -1.0;
        var x = frame.ToWorld(a, b);
        foreach (var c in cameras)
        {
            double dx = c[0] - x[0], dy = c[1] - x[1], dz = c[2] - x[2];
            var dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var h = (frame.Normal[0] * dx) + (frame.Normal[1] * dy) + (frame.Normal[2] * dz);
            if (dist > 0 && h / dist > bestCos)
            {
                bestCos = h / dist;
                best = c;
            }
        }

        return best;
    }
}
