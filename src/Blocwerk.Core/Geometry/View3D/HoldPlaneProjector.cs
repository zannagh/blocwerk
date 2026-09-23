// <copyright file="HoldPlaneProjector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>A photo-normalised point (x right, y down, 0..1) → facet plane mm (a right, b up); NaN when unmappable.</summary>
/// <param name="x">Normalised x in the hold's photo.</param>
/// <param name="y">Normalised y in the hold's photo.</param>
/// <returns>The plane point.</returns>
public delegate (double A, double B) PhotoToPlane(double x, double y);

/// <summary>
/// Per (photo, facet) perspective mappings from a hold's photo onto its facet, so a traced outline can
/// be mapped vertex by vertex (a foreshortened facet squeezes the outline in the photo; a centre +
/// scale approximation would keep the squeeze). Preferred: the photo's stored marker observations,
/// fitted exactly as ingest did (<see cref="MarkerPlaneMapper"/>). Otherwise a robust homography
/// fitted to the photo's own placed holds on that facet (their normalised centres against their
/// stored plane positions), which needs no markers at all.
/// </summary>
public sealed class HoldPlaneProjector
{
    /// <summary>Placed holds a (photo, facet) needs before a homography is fitted to their centres.</summary>
    public const int MinHoldsForFit = 8;

    /// <summary>Plane-space inlier threshold of that fit: centres further off are treated as outliers.</summary>
    public const double HoldFitThresholdMm = 40;

    private readonly Dictionary<(Wall3DPhotoKey Photo, string Facet), PhotoToPlane> markerMaps;
    private readonly Dictionary<(Wall3DPhotoKey Photo, string Facet), List<Hold>> holdsByPhotoFacet;
    private readonly Dictionary<(Wall3DPhotoKey Photo, string Facet), PhotoToPlane?> holdFits = [];

    private HoldPlaneProjector(
        Dictionary<(Wall3DPhotoKey Photo, string Facet), PhotoToPlane> byMarkers,
        Dictionary<(Wall3DPhotoKey Photo, string Facet), List<Hold>> byPhotoFacet)
    {
        markerMaps = byMarkers;
        holdsByPhotoFacet = byPhotoFacet;
    }

    /// <summary>The photo a hold is normalised to.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Its photo key.</returns>
    public static Wall3DPhotoKey PhotoOf(Hold hold) => new(hold.WallPanelId, hold.Generation);

    /// <summary>Builds the projector for one wall.</summary>
    /// <param name="placedHolds">Live holds that carry a facet id and plane position.</param>
    /// <param name="document">The wall's active model.</param>
    /// <param name="photos">Stored marker observations per photo (may be empty).</param>
    /// <returns>The projector.</returns>
    public static HoldPlaneProjector Create(
        IEnumerable<Hold> placedHolds,
        WallGeometryDocument document,
        IReadOnlyDictionary<Wall3DPhotoKey, Wall3DPhotoMarkers>? photos)
    {
        var markerMaps = new Dictionary<(Wall3DPhotoKey Photo, string Facet), PhotoToPlane>();
        foreach (var (key, photo) in photos ?? new Dictionary<Wall3DPhotoKey, Wall3DPhotoMarkers>())
        {
            if (photo.Markers.Count == 0 || !(photo.Scale > 0))
            {
                continue;
            }

            var scale = photo.Scale;
            foreach (var map in MarkerPlaneMapper.Map(photo.Markers, document).Facets)
            {
                if (map.FacetId is { } facetId)
                {
                    markerMaps[(key, facetId)] = (x, y) => map.ImageToPlaneMm(x * scale, y * scale);
                }
            }
        }

        var groups = placedHolds
            .Where(h => h.FacetId is not null && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue)
            .GroupBy(h => (PhotoOf(h), h.FacetId!))
            .ToDictionary(g => g.Key, g => g.ToList());
        return new HoldPlaneProjector(markerMaps, groups);
    }

    /// <summary>The mapping for a hold's photo onto the hold's facet, or null when there is none.</summary>
    /// <param name="hold">A placed hold.</param>
    /// <returns>The mapping and how it was obtained, or null.</returns>
    public (PhotoToPlane Map, Wall3DShapeSource Source)? For(Hold hold)
    {
        if (hold.FacetId is not { } facetId)
        {
            return null;
        }

        var key = (PhotoOf(hold), facetId);
        if (markerMaps.TryGetValue(key, out var byMarkers))
        {
            return (byMarkers, Wall3DShapeSource.Markers);
        }

        if (!holdFits.TryGetValue(key, out var fitted))
        {
            fitted = holdsByPhotoFacet.TryGetValue(key, out var holds) ? FitToHolds(holds) : null;
            holdFits[key] = fitted;
        }

        return fitted is null ? null : (fitted, Wall3DShapeSource.HoldFit);
    }

    /// <summary>
    /// A robust homography from the holds' normalised centres to their stored plane positions. Null
    /// with too few holds, a degenerate layout, or when fewer than half of them agree on one mapping.
    /// </summary>
    private static PhotoToPlane? FitToHolds(List<Hold> holds)
    {
        if (holds.Count < MinHoldsForFit)
        {
            return null;
        }

        var pairs = holds
            .OrderBy(h => h.Id)
            .Select(h => new PointCorrespondence(h.X, h.Y, h.PlaneAMm!.Value, h.PlaneBMm!.Value))
            .ToList();
        var fit = RobustHomographyFitter.Fit(pairs, 4, HoldFitThresholdMm);
        var inliers = fit?.Inliers.Count(i => i) ?? 0;
        if (fit is null || inliers < MinHoldsForFit || inliers * 2 < pairs.Count)
        {
            return null;
        }

        var h = fit.Homography;
        return (x, y) => h.Apply(x, y);
    }
}
