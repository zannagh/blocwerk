// <copyright file="PanelCameraEstimator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// Where a hold photo was taken from, from its placed holds (photo position ↔ facet-plane position). Holds
/// well spread over non-coplanar facets resect it by DLT (<see cref="CameraResection"/>, no intrinsics
/// needed); (near-)coplanar holds — a photo of the main wall, whose few side-facet holds the DLT's trimming
/// drops — make that degenerate, so they (and a failed or implausible DLT) use the planar pose
/// (<see cref="PlanarResection"/>), which needs the photo's size. Either centre must lie in front of the
/// main facet at a plausible distance and reproject the holds with a bounded median error. Pure: no I/O.
/// </summary>
public static class PanelCameraEstimator
{
    /// <summary>Nearest plausible camera, mm in front of the main facet.</summary>
    public const double MinDistanceMm = 500;

    /// <summary>Farthest plausible camera, mm in front of the main facet.</summary>
    public const double MaxDistanceMm = 15000;

    /// <summary>Largest accepted median reprojection error, as a share of the photo's longer side.</summary>
    public const double MaxMedianErrorShare = 0.02;

    /// <summary>Holds off the main facet's plane below this share (RMS, relative to the in-plane spread) are coplanar.</summary>
    public const double PlanarityRatio = 0.05;

    /// <summary>Estimates one photo's camera.</summary>
    /// <param name="points">The photo's placed holds, in a stable order.</param>
    /// <param name="frames">The model's facet frames.</param>
    /// <param name="photo">The photo's size and focal length, or null (then only the DLT can serve).</param>
    /// <returns>The estimate; its centre is null when no method passed the checks.</returns>
    public static PanelCameraEstimate Estimate(
        IReadOnlyList<PlacedPhotoPoint> points, IReadOnlyDictionary<string, FacetFrame> frames, PanelPhotoInfo? photo)
    {
        var usable = points.Where(p => frames.ContainsKey(p.FacetId)).ToList();
        if (usable.Count < CameraResection.MinPairs)
        {
            return PanelCameraEstimate.Rejected(points.Count, "too few placed holds");
        }

        var main = frames[usable.GroupBy(p => p.FacetId, StringComparer.Ordinal).MaxBy(g => g.Count())!.Key];
        var world = usable.Select(p => frames[p.FacetId].ToWorld(p.A, p.B)).ToList();
        var rejection = "the holds are coplanar and the photo's size is unknown";
        if (!IsCoplanar(world, main))
        {
            var dlt = Dlt(usable, world, photo, points.Count);
            if (Check(dlt, main, world, photo) is { } checkedDlt)
            {
                return checkedDlt;
            }

            rejection = dlt.Centre is null ? "DLT resection degenerate" : "DLT centre implausible";
        }

        if (photo is null)
        {
            return PanelCameraEstimate.Rejected(points.Count, rejection);
        }

        var planar = PlanarResection.Estimate(usable, frames, photo);
        return Check(planar, main, world, photo) ?? PanelCameraEstimate.Rejected(points.Count, planar.Rejection ?? "planar centre implausible");
    }

    /// <summary>
    /// The estimate with its distance filled in when its centre is in front of the main facet at a plausible
    /// distance and its median reprojection error is bounded; null otherwise.
    /// </summary>
    /// <param name="estimate">The unchecked estimate.</param>
    /// <param name="main">The facet most holds are on.</param>
    /// <param name="world">The holds' world positions.</param>
    /// <param name="photo">The photo, when known.</param>
    /// <returns>The checked estimate, or null.</returns>
    public static PanelCameraEstimate? Check(PanelCameraEstimate estimate, FacetFrame main, IReadOnlyList<double[]> world, PanelPhotoInfo? photo)
    {
        if (estimate.Centre is not { } c || !c.All(double.IsFinite))
        {
            return null;
        }

        var centroid = Centroid(world);
        var distance = Dot(main.Normal, Sub(c, centroid));
        var maxError = photo is null ? MaxMedianErrorShare : MaxMedianErrorShare * Math.Max(photo.Width, photo.Height);
        if (distance < MinDistanceMm || distance > MaxDistanceMm || estimate.MedianErrorPx is not { } error || !(error <= maxError))
        {
            return null;
        }

        return estimate with { DistanceMm = distance };
    }

    /// <summary>The DLT resection on the photo's normalised positions (error reported in px when the size is known).</summary>
    private static PanelCameraEstimate Dlt(List<PlacedPhotoPoint> usable, List<double[]> world, PanelPhotoInfo? photo, int count)
    {
        if (CameraResection.Resect(world, usable.Select(p => (p.X, p.Y)).ToList()) is not { } fit)
        {
            return PanelCameraEstimate.Rejected(count, "DLT resection degenerate");
        }

        // Normalised x and y are scaled differently; the longer side bounds the px error from above.
        var error = photo is null ? fit.MedianError : fit.MedianError * Math.Max(photo.Width, photo.Height);
        return new PanelCameraEstimate(fit.Centre, "dlt", count, fit.Kept, null, error, null, null);
    }

    /// <summary>Whether the points' RMS distance off the main facet's plane is tiny against their spread along it.</summary>
    private static bool IsCoplanar(List<double[]> world, FacetFrame main)
    {
        var centroid = Centroid(world);
        double off = 0, along = 0;
        foreach (var p in world)
        {
            var d = Sub(p, centroid);
            var n = Dot(main.Normal, d);
            off += n * n;
            along += Dot(d, d) - (n * n);
        }

        return along <= 0 || Math.Sqrt(off / along) < PlanarityRatio;
    }

    private static double[] Centroid(IReadOnlyList<double[]> world) =>
        [world.Average(p => p[0]), world.Average(p => p[1]), world.Average(p => p[2])];

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
