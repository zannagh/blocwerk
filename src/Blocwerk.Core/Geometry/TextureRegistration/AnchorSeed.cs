// <copyright file="AnchorSeed.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// A predicted photo px → texture px homography for one facet, fitted robustly to <see cref="PlaneAnchor"/>s of
/// the photo on that facet. Hold centres stand proud of the plane and were placed through an earlier fit, so an
/// anchor carries a few centimetres of error: the fit is only a seed the matcher searches around
/// (the photo session searches about 9 cm around a seed), and the matches it finds are judged like any other. Pure.
/// </summary>
public static class AnchorSeed
{
    /// <summary>Fewest anchors on a facet (and fewest the fit must explain) to seed it.</summary>
    public const int MinAnchors = 8;

    /// <summary>Plane-space inlier threshold of the anchor fit, mm (hold parallax and earlier fit error).</summary>
    public const double InlierThresholdMm = 60;

    /// <summary>The anchors' inliers must span at least this share of the photo along both x and y, so the fit does not extrapolate wildly.</summary>
    public const double MinSpanShare = 0.1;

    /// <summary>The seed for <paramref name="frame"/>'s facet, or null when too few or too inconsistent anchors lie on it.</summary>
    /// <param name="anchors">The photo's anchors (any facet).</param>
    /// <param name="frame">The target texture's grid.</param>
    /// <param name="width">Photo width, px.</param>
    /// <param name="height">Photo height, px.</param>
    /// <returns>The row-major homography and the number of anchors it explains, or null.</returns>
    public static (double[] Seed, int Inliers)? Fit(IReadOnlyList<PlaneAnchor> anchors, TexturePlaneFrame frame, int width, int height)
    {
        var pairs = anchors
            .Where(p => p.FacetId == frame.FacetId && double.IsFinite(p.A) && double.IsFinite(p.B))
            .Select(p =>
            {
                var (u, v) = frame.ToPixel(p.A, p.B);
                return new PointCorrespondence(p.X * width, p.Y * height, u, v);
            })
            .ToList();
        if (pairs.Count < MinAnchors || !frame.IsValid || width <= 0 || height <= 0)
        {
            return null;
        }

        var mmPerPx = (frame.MmPerPxA + frame.MmPerPxB) / 2;
        var fit = RobustHomographyFitter.Fit(pairs, 4, InlierThresholdMm / mmPerPx);
        var inliers = fit is null ? [] : pairs.Where((_, i) => fit.Inliers[i]).ToList();
        if (fit is null || inliers.Count < MinAnchors || !Spread(inliers, width, height))
        {
            return null;
        }

        return (fit.Homography.Coefficients, inliers.Count);
    }

    private static bool Spread(List<PointCorrespondence> inliers, int width, int height)
    {
        var spanX = inliers.Max(p => p.SrcX) - inliers.Min(p => p.SrcX);
        var spanY = inliers.Max(p => p.SrcY) - inliers.Min(p => p.SrcY);
        return spanX >= MinSpanShare * width && spanY >= MinSpanShare * height;
    }
}
