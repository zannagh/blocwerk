using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Detection.Enrichment;

/// <summary>
/// Pure geometry for the marker pass: which facet a hold sits on, where on it, and how big it is in mm.
/// All inputs are in the photo's RAW pixel grid (the frame hold X/Y are stored in).
/// </summary>
public static class HoldMetricMeasurer
{
    /// <summary>Slack around a facet's <c>extentMm</c> before a hold counts as off the facet.</summary>
    public const double ExtentMarginMm = 100.0;

    private const int CircleVertices = 16;

    /// <summary>
    /// Measures a hold against a wall model: the candidate facets are those whose extent (+ margin)
    /// contains the mapped hold centre; MultiMarker fits beat SingleMarker ones, then the facet whose
    /// markers are nearest the hold in the image wins.
    /// </summary>
    /// <param name="outlinePx">The hold's outline in pixels.</param>
    /// <param name="centerPx">The hold's centre in pixels.</param>
    /// <param name="mapping">The photo's per-facet mappings.</param>
    /// <param name="document">The wall model (for facet extents).</param>
    /// <param name="markers">The photo's detected markers (for the nearest-marker tie-break).</param>
    /// <param name="holesPx">The outline's interior holes in pixels (<see cref="HolesPx"/>); subtracted from the area.</param>
    /// <returns>The measurement, or null when no facet contains the hold.</returns>
    public static HoldMetric? MeasureOnFacets(
        IReadOnlyList<MarkerPoint> outlinePx,
        MarkerPoint centerPx,
        MarkerPlaneMapping mapping,
        WallGeometryDocument document,
        IReadOnlyList<DetectedMarker> markers,
        IReadOnlyList<IReadOnlyList<MarkerPoint>>? holesPx = null)
    {
        var best = mapping.Facets
            .Where(f => ContainsCentre(f, centerPx, document))
            .OrderBy(f => f.Mode == PlaneMappingMode.MultiMarker ? 0 : 1)
            .ThenBy(f => NearestMarkerDistance(f, centerPx, markers))
            .FirstOrDefault();
        if (best is null)
        {
            return null;
        }

        var source = best.Mode == PlaneMappingMode.MultiMarker ? HoldMetric.MultiMarker : HoldMetric.SingleMarker;
        return Measure(best, outlinePx, holesPx, centerPx, source);
    }

    /// <summary>
    /// Measures a hold without a wall model: sizes only, in the frame of the marker nearest to it.
    /// </summary>
    /// <param name="outlinePx">The hold's outline in pixels.</param>
    /// <param name="centerPx">The hold's centre in pixels.</param>
    /// <param name="localMaps">One local frame per marker (<see cref="MarkerPlaneMapper.MapLocal(IReadOnlyList{DetectedMarker}, double)"/>).</param>
    /// <param name="markers">The detected markers.</param>
    /// <param name="holesPx">The outline's interior holes in pixels; subtracted from the area.</param>
    /// <returns>Sizes with no facet/position, or null when there is no usable marker.</returns>
    public static HoldMetric? MeasureLocal(
        IReadOnlyList<MarkerPoint> outlinePx,
        MarkerPoint centerPx,
        IReadOnlyList<FacetPlaneMap> localMaps,
        IReadOnlyList<DetectedMarker> markers,
        IReadOnlyList<IReadOnlyList<MarkerPoint>>? holesPx = null)
    {
        var nearest = localMaps.MinBy(m => NearestMarkerDistance(m, centerPx, markers));
        var measured = nearest is null ? null : Measure(nearest, outlinePx, holesPx, centerPx, HoldMetric.LocalMarker);
        return measured is null ? null : measured with { FacetId = null, PlaneAMm = null, PlaneBMm = null };
    }

    /// <summary>
    /// The hold's outline in pixels: the fresh outline when it is a real contour, else the stored shape
    /// points (≥ 3), else the detector circle (radius normalized by the image's longer side).
    /// </summary>
    /// <param name="hold">The hold.</param>
    /// <param name="outline">This run's outline of the hold, if any.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The polygon in pixels.</returns>
    public static List<MarkerPoint> OutlinePx(Hold hold, HoldOutlineResult? outline, int width, int height)
    {
        IEnumerable<NormalizedPoint>? polygon = null;
        if (outline is not null && outline.Method != HoldOutlineMethod.CircleFallback && outline.Polygon.Count >= 3)
        {
            polygon = outline.Polygon;
        }
        else if (hold.ShapePoints is { Count: >= 3 } shape)
        {
            polygon = HoldOutlineGeometry.ToPolygon(shape, hold.X, hold.Y);
        }

        if (polygon is not null)
        {
            return polygon.Select(p => new MarkerPoint(p.X * width, p.Y * height)).ToList();
        }

        var r = hold.Radius * Math.Max(width, height);
        return Enumerable.Range(0, CircleVertices)
            .Select(i => 2 * Math.PI * i / CircleVertices)
            .Select(t => new MarkerPoint((hold.X * width) + (r * Math.Cos(t)), (hold.Y * height) + (r * Math.Sin(t))))
            .ToList();
    }

    /// <summary>
    /// The interior holes (pocket/donut) belonging to the outline <see cref="OutlinePx"/> picked: the fresh
    /// outline's holes when it is a real contour, else the hold's stored <see cref="Hold.ShapeHoles"/> when its
    /// stored shape is used, else none. Rings are relative to the hold centre, like the shape points.
    /// </summary>
    /// <param name="hold">The hold.</param>
    /// <param name="outline">This run's outline of the hold, if any.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>The hole rings in pixels (possibly empty).</returns>
    public static List<IReadOnlyList<MarkerPoint>> HolesPx(Hold hold, HoldOutlineResult? outline, int width, int height)
    {
        List<List<ShapePoint>>? rings;
        double ax = hold.X, ay = hold.Y;
        if (outline is not null && outline.Method != HoldOutlineMethod.CircleFallback && outline.Polygon.Count >= 3)
        {
            rings = outline.ShapeHoles;
            (ax, ay) = (outline.AnchorX, outline.AnchorY);
        }
        else
        {
            rings = hold.ShapePoints is { Count: >= 3 } ? hold.ShapeHoles : null;
        }

        return (rings ?? [])
            .Where(r => r.Count >= 3)
            .Select(r => (IReadOnlyList<MarkerPoint>)r.Select(sp => new MarkerPoint((ax + sp.Dx) * width, (ay + sp.Dy) * height)).ToList())
            .ToList();
    }

    /// <summary>Absolute polygon area by the shoelace formula.</summary>
    /// <param name="points">The vertices in order.</param>
    /// <returns>The area (0 for fewer than three points).</returns>
    public static double ShoelaceArea(IReadOnlyList<(double A, double B)> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a1, b1) = points[i];
            var (a2, b2) = points[(i + 1) % points.Count];
            sum += (a1 * b2) - (a2 * b1);
        }

        return Math.Abs(sum) / 2.0;
    }

    private static HoldMetric? Measure(
        FacetPlaneMap map,
        IReadOnlyList<MarkerPoint> outlinePx,
        IReadOnlyList<IReadOnlyList<MarkerPoint>>? holesPx,
        MarkerPoint centerPx,
        string source)
    {
        var centre = map.ImageToPlaneMm(centerPx.X, centerPx.Y);
        var plane = outlinePx.Select(p => map.ImageToPlaneMm(p.X, p.Y)).ToList();
        if (!IsFinite(centre) || plane.Count < 3 || !plane.All(IsFinite))
        {
            return null;
        }

        // A pocket's hole is not hold surface: the metric area is the outer ring minus its holes (never < 0).
        var holeArea = (holesPx ?? [])
            .Select(ring => ring.Select(p => map.ImageToPlaneMm(p.X, p.Y)).ToList())
            .Where(ring => ring.Count >= 3 && ring.All(IsFinite))
            .Sum(ShoelaceArea);

        return new HoldMetric(
            plane.Max(p => p.A) - plane.Min(p => p.A),
            plane.Max(p => p.B) - plane.Min(p => p.B),
            Math.Max(0, ShoelaceArea(plane) - holeArea),
            map.FacetId,
            centre.A,
            centre.B,
            source);
    }

    private static bool ContainsCentre(FacetPlaneMap map, MarkerPoint centerPx, WallGeometryDocument document)
    {
        var centre = map.ImageToPlaneMm(centerPx.X, centerPx.Y);
        if (!IsFinite(centre) || map.FacetId is null)
        {
            return false;
        }

        // A facet without an extent cannot rule the hold out; it still loses the tie-breaks honestly.
        if (document.FindFacet(map.FacetId)?.Facet.ExtentMm is not { } extent)
        {
            return true;
        }

        return centre.A >= extent.AMin - ExtentMarginMm && centre.A <= extent.AMax + ExtentMarginMm
               && centre.B >= extent.BMin - ExtentMarginMm && centre.B <= extent.BMax + ExtentMarginMm;
    }

    private static double NearestMarkerDistance(FacetPlaneMap map, MarkerPoint centerPx, IReadOnlyList<DetectedMarker> markers)
    {
        var distances = markers
            .Where(m => map.MarkerIds.Contains(m.Id))
            .Select(m => Math.Sqrt(Math.Pow(m.CenterPx.X - centerPx.X, 2) + Math.Pow(m.CenterPx.Y - centerPx.Y, 2)))
            .ToList();
        return distances.Count == 0 ? double.MaxValue : distances.Min();
    }

    private static bool IsFinite((double A, double B) p) => double.IsFinite(p.A) && double.IsFinite(p.B);
}
