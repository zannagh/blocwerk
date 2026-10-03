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

    /// <summary>Margin around the placed holds' bounds a re-placed centre may still fall in.</summary>
    public const double PlacementMarginMm = 150;

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
    public (PhotoToPlane Map, Wall3DShapeSource Source)? For(Hold hold) =>
        hold.FacetId is { } facetId ? For(PhotoOf(hold), facetId) : null;

    /// <summary>
    /// Places a hold that has no (or a stale) facet position from its photo alone: every facet the photo
    /// maps onto is tried — the hold's own facet first, then marker-mapped facets, then those with the most
    /// placed holds — and the first whose mapping lands the centre inside that facet (its extent, else the
    /// bounds of the photo's placed holds on it plus <see cref="PlacementMarginMm"/>) wins. With no facet
    /// containing it, the best-ranked finite mapping is returned. Null when the photo maps onto nothing.
    /// </summary>
    /// <param name="hold">The hold, with its photo stamp (<see cref="Hold.WallPanelId"/>, generation) and centre.</param>
    /// <param name="extents">Known facet extents, by facet id; optional.</param>
    /// <returns>The placement, or null.</returns>
    public HoldPlaneFit? Place(Hold hold, IReadOnlyDictionary<string, PlaneRectMm>? extents = null)
    {
        var photo = PhotoOf(hold);
        var facets = markerMaps.Keys.Where(k => k.Photo == photo).Select(k => k.Facet)
            .Concat(holdsByPhotoFacet.Keys.Where(k => k.Photo == photo).Select(k => k.Facet))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(f => f == hold.FacetId)
            .ThenByDescending(f => markerMaps.ContainsKey((photo, f)))
            .ThenByDescending(f => holdsByPhotoFacet.TryGetValue((photo, f), out var l) ? l.Count : 0)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        HoldPlaneFit? fallback = null;
        foreach (var facet in facets)
        {
            if (For(photo, facet) is not { } mapping)
            {
                continue;
            }

            var (a, b) = mapping.Map(hold.X, hold.Y);
            if (!double.IsFinite(a) || !double.IsFinite(b))
            {
                continue;
            }

            var fit = new HoldPlaneFit(facet, a, b, mapping.Map, mapping.Source);
            if (SupportOf(photo, facet, extents) is not { } support || Inside(support, a, b))
            {
                return fit;
            }

            fallback ??= fit;
        }

        return fallback;
    }

    private (PhotoToPlane Map, Wall3DShapeSource Source)? For(Wall3DPhotoKey photo, string facetId)
    {
        var key = (photo, facetId);
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

    private PlaneRectMm? SupportOf(Wall3DPhotoKey photo, string facet, IReadOnlyDictionary<string, PlaneRectMm>? extents)
    {
        if (extents is not null && extents.TryGetValue(facet, out var extent) && extent.Area > 0)
        {
            return extent;
        }

        if (!holdsByPhotoFacet.TryGetValue((photo, facet), out var holds)
            || PlaneRectMm.Bounds(holds.Select(h => (h.PlaneAMm!.Value, h.PlaneBMm!.Value))) is not { } r)
        {
            return null;
        }

        return new PlaneRectMm(r.AMin - PlacementMarginMm, r.AMax + PlacementMarginMm, r.BMin - PlacementMarginMm, r.BMax + PlacementMarginMm);
    }

    private static bool Inside(PlaneRectMm r, double a, double b) => a >= r.AMin && a <= r.AMax && b >= r.BMin && b <= r.BMax;

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

/// <summary>Where <see cref="HoldPlaneProjector.Place"/> put a hold, and the mapping it used.</summary>
/// <param name="FacetId">The facet.</param>
/// <param name="PlaneAMm">Centre, facet plane a (mm).</param>
/// <param name="PlaneBMm">Centre, facet plane b (mm).</param>
/// <param name="Map">The photo → facet mapping, for the outline.</param>
/// <param name="Source">How the mapping was obtained.</param>
public sealed record HoldPlaneFit(string FacetId, double PlaneAMm, double PlaneBMm, PhotoToPlane Map, Wall3DShapeSource Source);
