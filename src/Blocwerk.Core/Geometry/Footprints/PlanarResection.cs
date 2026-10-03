// <copyright file="PlanarResection.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// A hold photo's camera from its placed holds when they (nearly) all lie on one facet, where a DLT without
/// known intrinsics is degenerate: a robust plane → photo px homography of the main facet's holds, decomposed
/// with K by <see cref="CameraPose"/>, the pose the texture registration recovers the same way. K has square
/// pixels, the principal point at the centre, and of the photo's EXIF focal length and the one self-calibrated
/// from the homography whichever reprojects the placed holds better. Pure: no I/O.
/// </summary>
public static class PlanarResection
{
    /// <summary>Inlier threshold of the homography fit, as a share of the photo's longer side.</summary>
    public const double InlierShare = 0.01;

    /// <summary>The camera of the photo, or a rejection; the centre is not yet checked for plausibility.</summary>
    /// <param name="points">The photo's placed holds (all facets; the one with most holds anchors the pose).</param>
    /// <param name="frames">The model's facet frames.</param>
    /// <param name="photo">The photo's size and focal length.</param>
    /// <returns>The estimate.</returns>
    public static PanelCameraEstimate Estimate(
        IReadOnlyList<PlacedPhotoPoint> points, IReadOnlyDictionary<string, FacetFrame> frames, PanelPhotoInfo photo)
    {
        var side = (double)Math.Max(photo.Width, photo.Height);
        var usable = points.Where(p => frames.ContainsKey(p.FacetId)).ToList();
        var main = usable.GroupBy(p => p.FacetId, StringComparer.Ordinal).MaxBy(g => g.Count())?.ToList();
        if (main is null || main.Count < CameraResection.MinPairs || side <= 0)
        {
            return PanelCameraEstimate.Rejected(points.Count, "too few holds on one facet for a planar pose");
        }

        var pairs = main.Select(p => new PointCorrespondence(p.A, p.B, p.X * photo.Width, p.Y * photo.Height)).ToList();
        var fit = RobustHomographyFitter.Fit(pairs, 4, InlierShare * side);
        var inliers = fit is null ? [] : main.Where((_, i) => fit.Inliers[i]).ToList();
        if (fit is null || inliers.Count < CameraResection.MinPairs || inliers.Count * 2 < main.Count)
        {
            return PanelCameraEstimate.Rejected(points.Count, "no consistent homography of the main facet's holds");
        }

        var g = fit.Homography.Coefficients;
        var (cx, cy) = (photo.Width / 2.0, photo.Height / 2.0);
        var (a, b) = (inliers.Average(p => p.A), inliers.Average(p => p.B));
        var frame = frames[main[0].FacetId];
        (double? Focal, string Method)[] focals =
        [
            (photo.FocalPx is > 0 ? photo.FocalPx : null, "planar (EXIF focal)"),
            (FacetViewPrediction.SelfCalibratedFocal(g, cx, cy, side), "planar (self-calibrated focal)"),
        ];

        // Both focal lengths when both are known: the one that reprojects all placed holds best wins.
        PanelCameraEstimate? best = null;
        foreach (var (focal, method) in focals)
        {
            if (focal is { } f && CameraPose.FromHomography(g, f, cx, cy, a, b, frame) is { } pose)
            {
                var median = MedianErrorPx(pose, usable, frames, photo);
                if (best is null || median < best.MedianErrorPx)
                {
                    best = new PanelCameraEstimate(pose.Centre, method, points.Count, inliers.Count, null, median, f, null);
                }
            }
        }

        return best ?? PanelCameraEstimate.Rejected(points.Count, "no focal length (no EXIF, and the view is too frontal to self-calibrate)");
    }

    /// <summary>Median distance, px, between each hold and its plane position projected by the pose.</summary>
    private static double MedianErrorPx(
        CameraPose pose, List<PlacedPhotoPoint> points, IReadOnlyDictionary<string, FacetFrame> frames, PanelPhotoInfo photo)
    {
        var toPhoto = new Dictionary<string, PlaneHomography>(StringComparer.Ordinal);
        var errors = new List<double>();
        foreach (var p in points)
        {
            if (!toPhoto.TryGetValue(p.FacetId, out var h))
            {
                h = toPhoto[p.FacetId] = pose.PlaneToPhoto(frames[p.FacetId]);
            }

            var (x, y) = h.Apply(p.A, p.B);
            var error = Math.Sqrt(Math.Pow(x - (p.X * photo.Width), 2) + Math.Pow(y - (p.Y * photo.Height), 2));
            errors.Add(double.IsFinite(error) ? error : double.MaxValue);
        }

        errors.Sort();
        return errors[errors.Count / 2];
    }
}
