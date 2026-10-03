// <copyright file="HoldPhotoOutline.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Where a hold shows in its facet's photo texture. The texture paints every plane point from ONE capture
/// photo (<see cref="TextureSourceMap"/>), so a point z mm proud of the plane shows displaced away from
/// that camera by z/(d − z)·(q − c) (c the camera's foot on the plane, d its height above it).
/// <list type="bullet">
/// <item>A hold on a volume stands far proud of the facet. Its flat position is where the PANEL photo's
/// ray through it meets the facet; walking that ray back up to the hold's mid height gives the hold itself,
/// which is then projected from the source camera. Without a panel camera, the protrusion's volume shift
/// (<see cref="HoldVolumeRemap"/>) stands in for the walk back.</item>
/// <item>A hold on the bare facet with a multi-view footprint: that already sits where the capture photos, on
/// average, show it (the footprint is their consensus), so only the source photo's DEVIATION from that
/// average is applied: <see cref="WallParallaxShare"/> of the body height times the difference of the
/// per-mm displacements. Measured on B3 (2026-09-24): the view-specific offset of a hold's blob grows
/// only ~0.15–0.3 mm per mm of body and view difference, because the contact region, not the top,
/// dominates what a flat outline has to match. Other shapes (circles, single-photo outlines) stay.</item>
/// </list>
/// </summary>
public static class HoldPhotoOutline
{
    /// <summary>Share of the body height whose parallax a bare-wall hold shows beyond the footprint's.</summary>
    public const double WallParallaxShare = 0.3;

    /// <summary>Height share (base → body top) at which a hold on a volume is projected.</summary>
    public const double VolumeHeightShare = 0.5;

    /// <summary>Largest displacement applied to a bare-wall hold, mm (larger means bad inputs).</summary>
    public const double MaxWallShiftMm = 15;

    /// <summary>Largest displacement applied to a hold on a volume, mm.</summary>
    public const double MaxVolumeShiftMm = 250;

    /// <summary>A reference camera must see the hold at most this steeply (as the footprint refinement).</summary>
    public const double MaxReferenceThetaDeg = HoldFootprintRefiner.MaxCaptureThetaDeg;

    /// <summary>The outline as the texture shows it, relative to the hold's plane centre; null when unchanged.</summary>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="hold">The placed hold (outline, protrusion).</param>
    /// <param name="source">The camera that painted the hold's spot.</param>
    /// <param name="cameras">All capture cameras of the model (the footprint's reference views).</param>
    /// <param name="panelCamera">The centre of the panel photo the hold was placed from (world mm), when known.</param>
    /// <returns>The ring <c>[da, db]</c>, or null.</returns>
    public static IReadOnlyList<double[]>? For(
        FacetFrame frame, Wall3DHold hold, SolvedCamera source, IReadOnlyList<SolvedCamera> cameras, double[]? panelCamera = null)
    {
        if (hold.Shape is not { Outline.Count: >= 3 } shape || hold.Protrusion is not { } p)
        {
            return null;
        }

        var shift = p.OnVolume ? (p.ShiftA, p.ShiftB) : (0.0, 0.0);
        var centre = (A: hold.PlaneA + shift.Item1, B: hold.PlaneB + shift.Item2);
        if (Foot(frame, source.Centre) is not { } src)
        {
            return null;
        }

        if (p.OnVolume)
        {
            var z = Math.Clamp(p.BaseMm + (VolumeHeightShare * Math.Max(p.HeightMm - p.BaseMm, 0)), 0, 0.8 * src.D);
            var k = z / (src.D - z);
            var panel = panelCamera is null ? null : Foot(frame, panelCamera);
            var ring = shape.Outline.Select(v =>
            {
                // the hold at height z: back up the panel ray from its flat position, or along the volume shift
                var (qa, qb) = panel is { } pc && pc.D > z / 0.8
                    ? (pc.A + ((hold.PlaneA + v[0] - pc.A) * (pc.D - z) / pc.D), pc.B + ((hold.PlaneB + v[1] - pc.B) * (pc.D - z) / pc.D))
                    : (centre.A + v[0], centre.B + v[1]);
                return (A: qa + (k * (qa - src.A)) - hold.PlaneA, B: qb + (k * (qb - src.B)) - hold.PlaneB);
            }).ToList();
            return Capped(ring, shape.Outline, MaxVolumeShiftMm);
        }

        // Only a multi-view footprint is the capture photos' consensus; a circle or single-photo shape sits
        // where ITS photo put it, whose parallax is not known here: those stay as drawn.
        if (shape.Source != Wall3DShapeSource.Footprint)
        {
            return null;
        }

        var refs = cameras.Where(c => Sees(frame, centre, c)).Select(c => Foot(frame, c.Centre)).OfType<(double A, double B, double D)>().ToList();
        if (refs.Count == 0)
        {
            return null;
        }

        var (ta, tb) = PerMm(centre, src);
        var (ma, mb) = (refs.Average(r => PerMm(centre, r).A), refs.Average(r => PerMm(centre, r).B));
        var h = WallParallaxShare * Math.Max(p.HeightMm, 0);
        var (da, db) = (h * (ta - ma), h * (tb - mb));
        return Capped(shape.Outline.Select(v => (A: v[0] + da, B: v[1] + db)).ToList(), shape.Outline, MaxWallShiftMm);
    }

    /// <summary>The camera's foot on the facet plane and its height above it (null when behind the facet).</summary>
    private static (double A, double B, double D)? Foot(FacetFrame frame, double[] camera)
    {
        var r = new[] { camera[0] - frame.Origin[0], camera[1] - frame.Origin[1], camera[2] - frame.Origin[2] };
        var d = Dot(r, frame.Normal);
        return d > 1 ? (Dot(r, frame.U), Dot(r, frame.V), d) : null;
    }

    /// <summary>Displacement per mm of height at the plane point seen from the camera foot.</summary>
    private static (double A, double B) PerMm((double A, double B) q, (double A, double B, double D) cam) =>
        ((q.A - cam.A) / cam.D, (q.B - cam.B) / cam.D);

    private static bool Sees(FacetFrame frame, (double A, double B) at, SolvedCamera camera)
    {
        if (HoldFootprintEstimator.ViewOf(frame, at, camera.Centre) is not { ThetaDeg: <= MaxReferenceThetaDeg })
        {
            return false;
        }

        var m = HoldFootprintRefiner.FrameMargin;
        return camera.Project(frame.ToWorld(at.A, at.B)) is { } px
            && px.X >= m * camera.Width && px.X <= (1 - m) * camera.Width
            && px.Y >= m * camera.Height && px.Y <= (1 - m) * camera.Height;
    }

    /// <summary>The ring rounded to 0.1 mm, or null when its centroid moved too far (or not at all).</summary>
    private static List<double[]>? Capped(List<(double A, double B)> ring, IReadOnlyList<double[]> original, double maxMm)
    {
        var moved = Math.Sqrt(Math.Pow(ring.Average(v => v.A) - original.Average(v => v[0]), 2)
                              + Math.Pow(ring.Average(v => v.B) - original.Average(v => v[1]), 2));
        if (!(moved <= maxMm) || moved < 0.5 || ring.Any(v => !double.IsFinite(v.A) || !double.IsFinite(v.B)))
        {
            return null;
        }

        return ring.Select(v => new[] { Math.Round(v.A, 1), Math.Round(v.B, 1) }).ToList();
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
