// <copyright file="PhotoTextureRegistration.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Turns one photo × facet-texture match into a photo → facet-plane mapping, or refuses it. A facet is a
/// plane, so its view in a photo and its flattened texture are related by exactly one homography: it is fitted
/// robustly (<see cref="RobustHomographyFitter"/>) to the matcher's photo px → texture px pairs, then chained
/// with the texture's own linear px → mm grid (<see cref="TexturePlaneFrame"/>). Pure: no I/O.
/// </summary>
public static class PhotoTextureRegistration
{
    /// <summary>
    /// Fewest inliers an accepted fit needs. Four points fit any homography; the coarse wall-photo matcher trusts
    /// 15 (<c>HomographyHelper.MinInliers</c>) only to find an overlap. Placing hundreds of holds on it needs far
    /// more consensus: a real facet view yields hundreds of guided matches, a wrong one a few dozen at best.
    /// </summary>
    public const int MinInliers = 40;

    /// <summary>
    /// Share of the photo's view of the facet the inliers' convex hull must span. Below it the fit is
    /// extrapolated over most of the facet, where a small rotation error grows into centimetres. Measured on
    /// the facet in view, not the whole photo, so a small facet at the photo's edge can still register.
    /// </summary>
    public const double MinCoverage = 0.20;

    /// <summary>
    /// Plane-space inlier threshold. Wood grain, bolt holes and marker corners register within 1-3 mm; the
    /// holds themselves stand 3-10 cm proud of the plane and shift with the viewpoint, so they fall out.
    /// </summary>
    public const double InlierThresholdMm = 8;

    /// <summary>A facet covering less of the photo than this is not in view enough to register.</summary>
    public const double MinOverlapShare = 0.02;

    /// <summary>
    /// Below <see cref="MinCoverage"/> a fit is still accepted when it is dense, precise and spread out: at least
    /// this many inliers, <see cref="MaxSpreadRmsMm"/> RMS, and inliers spanning <see cref="MinSpreadMm"/> (or
    /// half the facet, when it is smaller) along both plane axes. On a huge facet seen obliquely the matches cluster
    /// where the wall has texture (The Attic's right panel on the 6.7 m main wall: 8 % coverage, 3.9 mm RMS), yet
    /// hundreds of precise inliers spread over metres pin the plane down; a false fit concentrates its inliers on
    /// one repeated pattern and fails the spread or the RMS.
    /// </summary>
    public const int MinSpreadInliers = 150;

    /// <summary>Largest inlier RMS of a fit accepted on its spread.</summary>
    public const double MaxSpreadRmsMm = 6;

    /// <summary>Inlier span (5th to 95th percentile) a fit accepted on its spread needs along each plane axis.</summary>
    public const double MinSpreadMm = 800;

    /// <summary>Coverage floor even for a well-spread fit.</summary>
    public const double MinSpreadCoverage = 0.04;

    /// <summary>Registers one photo onto one facet texture.</summary>
    /// <param name="match">The matcher's correspondences (photo px → texture px).</param>
    /// <param name="photoWidth">Photo width, px.</param>
    /// <param name="photoHeight">Photo height, px.</param>
    /// <param name="frame">The texture's grid on the plane.</param>
    /// <param name="extent">The facet extent holds must fall into.</param>
    /// <returns>The registration, accepted or not.</returns>
    public static FacetRegistration Register(
        PhotoTextureMatch match, int photoWidth, int photoHeight, TexturePlaneFrame frame, PlaneRectMm extent)
    {
        var id = frame.FacetId;
        if (match.Failure is { } failure || match.Pairs.Count < 4 || !frame.IsValid || photoWidth <= 0 || photoHeight <= 0)
        {
            var why = match.Failure ?? (frame.IsValid ? "too few matches" : "the texture has no usable grid");
            return FacetRegistration.Rejected(id, extent, match.Pairs.Count, match.CoarseInliers, why);
        }

        var mmPerPx = (frame.MmPerPxA + frame.MmPerPxB) / 2;
        var fit = RobustHomographyFitter.Fit(match.Pairs, 4, InlierThresholdMm / mmPerPx);
        if (fit is null)
        {
            return FacetRegistration.Rejected(id, extent, match.Pairs.Count, match.CoarseInliers, "no homography fits the matches");
        }

        var inliers = match.Pairs.Where((_, i) => fit.Inliers[i]).ToList();
        if (inliers.Count < 4)
        {
            return FacetRegistration.Rejected(id, extent, match.Pairs.Count, match.CoarseInliers, $"{inliers.Count} inliers (needs {MinInliers})");
        }

        var normalised = PlaneHomography.FromCoefficients([photoWidth, 0, 0, 0, photoHeight, 0, 0, 0, 1]);
        var toPlane = normalised.Then(fit.Homography).Then(frame.PixelToPlane());
        var cx = inliers.Average(p => p.SrcX);
        var cy = inliers.Average(p => p.SrcY);
        var depthSign = Math.Sign(toPlane.Depth(cx / photoWidth, cy / photoHeight));
        var rmsMm = Math.Sqrt(inliers.Average(p => RobustHomographyFitter.ErrorSq(fit.Homography, p))) * mmPerPx;
        var coverage = PhotoCoverage.Measure(
            inliers.Select(p => (p.SrcX, p.SrcY)).ToList(), photoWidth, photoHeight, toPlane, depthSign, extent);
        var spread = PhotoCoverage.Spread(inliers.Select(p => (p.SrcX / photoWidth, p.SrcY / photoHeight)).ToList(), toPlane);
        var reason = Refusal(new FitEvidence(inliers.Count, rmsMm, coverage, spread, extent), Mirrored(fit.Homography, cx, cy));
        return new FacetRegistration(
            id, reason is null, match.Pairs.Count, inliers.Count, match.CoarseInliers, coverage.Coverage, coverage.PhotoShare,
            rmsMm, reason, toPlane, extent, depthSign, [.. inliers.Select(p => (p.SrcX / photoWidth, p.SrcY / photoHeight))]);
    }

    /// <summary>Why a fit is refused, or null when it is accepted.</summary>
    private static string? Refusal(FitEvidence e, bool mirrored)
    {
        if (e.Inliers < MinInliers)
        {
            return $"{e.Inliers} inliers (needs {MinInliers})";
        }

        if (mirrored)
        {
            return "the fit mirrors the photo";
        }

        if (e.Coverage.OverlapShare < MinOverlapShare)
        {
            return "the facet is barely in view";
        }

        if (e.Coverage.Coverage >= MinCoverage || WellSpread(e))
        {
            return null;
        }

        return $"the matches span {e.Coverage.Coverage:P0} of the facet in view (needs {MinCoverage:P0}, or {MinSpreadInliers} "
            + $"inliers spread {MinSpreadMm:F0} mm both ways; have {e.Inliers} over {e.Spread.A:F0} × {e.Spread.B:F0} mm)";
    }

    /// <summary>The low-coverage exception: many precise inliers spread along both axes of the facet.</summary>
    private static bool WellSpread(FitEvidence e) =>
        e.Inliers >= MinSpreadInliers
        && e.RmsMm <= MaxSpreadRmsMm
        && e.Coverage.Coverage >= MinSpreadCoverage
        && e.Spread.A >= Math.Min(MinSpreadMm, (e.Extent.AMax - e.Extent.AMin) / 2)
        && e.Spread.B >= Math.Min(MinSpreadMm, (e.Extent.BMax - e.Extent.BMin) / 2);

    /// <summary>A photo and a texture are both y-down views of the same side of the plane: a real fit keeps orientation.</summary>
    private static bool Mirrored(PlaneHomography h, double x, double y)
    {
        var (dxx, dxy, dyx, dyy) = h.Jacobian(x, y);
        return (dxx * dyy) - (dxy * dyx) <= 0;
    }
}

/// <summary>What a fit's acceptance is judged on.</summary>
/// <param name="Inliers">Inlier count.</param>
/// <param name="RmsMm">Inlier RMS on the plane, mm.</param>
/// <param name="Coverage">Coverage of the facet in view.</param>
/// <param name="Spread">Inlier span along a and b, mm.</param>
/// <param name="Extent">The facet extent.</param>
internal readonly record struct FitEvidence(int Inliers, double RmsMm, PhotoCoverageResult Coverage, (double A, double B) Spread, PlaneRectMm Extent);
