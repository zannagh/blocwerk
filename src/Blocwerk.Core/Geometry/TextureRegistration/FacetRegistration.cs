// <copyright file="FacetRegistration.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// One photo registered (or not) onto one facet texture: the fit's evidence, and — when accepted — the
/// mapping from the photo's normalised coordinates (0..1, x right, y down) onto the facet plane (mm).
/// </summary>
/// <param name="FacetId">The facet.</param>
/// <param name="Accepted">Whether the mapping may place holds.</param>
/// <param name="Matches">Correspondences the matcher returned.</param>
/// <param name="Inliers">Correspondences the fitted homography explains.</param>
/// <param name="CoarseInliers">Inliers of the matcher's coarse stage.</param>
/// <param name="Coverage">Share of the photo's view of the facet spanned by the inliers (0..1).</param>
/// <param name="PhotoShare">Share of the whole photo spanned by the inliers (0..1).</param>
/// <param name="RmsMm">Reprojection RMS of the inliers on the plane, mm; null without a fit.</param>
/// <param name="Reason">Why it was rejected, or null.</param>
/// <param name="PhotoToPlane">Normalised photo → plane mm; null without a fit.</param>
/// <param name="Extent">The facet's extent a placed hold must fall into.</param>
/// <param name="DepthSign">Sign of the mapping's projective depth on the inliers; points with the other sign lie beyond its horizon.</param>
public sealed record FacetRegistration(
    string FacetId,
    bool Accepted,
    int Matches,
    int Inliers,
    int CoarseInliers,
    double Coverage,
    double PhotoShare,
    double? RmsMm,
    string? Reason,
    PlaneHomography? PhotoToPlane,
    PlaneRectMm Extent,
    int DepthSign = 1)
{
    /// <summary>A rejected registration without a fit.</summary>
    /// <param name="facetId">The facet.</param>
    /// <param name="extent">Its extent.</param>
    /// <param name="matches">Correspondences found.</param>
    /// <param name="coarseInliers">Coarse inliers.</param>
    /// <param name="reason">Why.</param>
    /// <returns>The registration.</returns>
    public static FacetRegistration Rejected(string facetId, PlaneRectMm extent, int matches, int coarseInliers, string reason) =>
        new(facetId, false, matches, 0, coarseInliers, 0, 0, null, reason, null, extent);

    /// <summary>Maps a normalised photo point onto the plane (NaN when it cannot).</summary>
    /// <param name="x">Normalised x.</param>
    /// <param name="y">Normalised y.</param>
    /// <returns>Plane (a, b) in mm.</returns>
    public (double A, double B) Map(double x, double y) =>
        PhotoToPlane is { } h && Math.Sign(h.Depth(x, y)) == DepthSign ? h.Apply(x, y) : (double.NaN, double.NaN);
}
