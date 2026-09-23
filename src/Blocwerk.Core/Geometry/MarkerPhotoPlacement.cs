using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry;

/// <summary>Where on one facet a photo looks, in that facet's plane mm.</summary>
public sealed record FacetPlacement
{
    public required int SegmentIndex { get; init; }

    public required string FacetId { get; init; }

    public required PlaneMappingMode Mode { get; init; }

    public required IReadOnlyList<int> MarkerIds { get; init; }

    /// <summary>Bounding box of the supporting markers' corners: the part of the facet surely in the photo.</summary>
    public required PlaneRectMm MarkersBoundsMm { get; init; }

    /// <summary>
    /// Bounding box of the photo's frame projected onto the facet, clipped to the facet's
    /// <see cref="WallGeometryFacet.ExtentMm"/> when known. Rough: the homography extrapolates
    /// (and ignores lens distortion) away from the markers, more so in <see cref="PlaneMappingMode.SingleMarker"/>.
    /// Null when the projected frame misses the extent entirely.
    /// </summary>
    public PlaneRectMm? VisibleBoundsMm { get; init; }

    /// <summary>Visible area / facet extent area (0..1), when the extent is known.</summary>
    public double? ExtentCoverage { get; init; }
}

/// <summary>
/// Reports which segments/facets a photo shows and roughly which part of each — the input for
/// auto-assigning uploaded photos to wall panels.
/// </summary>
public static class MarkerPhotoPlacement
{
    private const int SamplesPerEdge = 16;

    /// <summary>Places a photo from its detection result, ordered by segment then facet.</summary>
    public static IReadOnlyList<FacetPlacement> Place(MarkerDetectionResult detection, WallGeometryDocument document)
    {
        var mapping = MarkerPlaneMapper.Map(detection.Markers, document);
        var placements = new List<FacetPlacement>();
        foreach (var map in mapping.Facets)
        {
            var located = document.FindFacet(map.FacetId!);
            var extent = located?.Facet.ExtentMm;
            var visible = ProjectFrame(map, detection.ImageWidth, detection.ImageHeight);
            if (visible is not null && extent is not null)
            {
                visible = visible.Value.Intersect(extent.Value);
            }

            double? coverage = extent is { Area: > 0 } e ? (visible?.Area ?? 0) / e.Area : null;
            placements.Add(new FacetPlacement
            {
                SegmentIndex = map.SegmentIndex,
                FacetId = map.FacetId!,
                Mode = map.Mode,
                MarkerIds = map.MarkerIds,
                MarkersBoundsMm = MarkerBounds(map, document),
                VisibleBoundsMm = visible,
                ExtentCoverage = coverage,
            });
        }

        return placements
            .OrderBy(p => p.SegmentIndex)
            .ThenBy(p => p.FacetId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Projects points sampled along the image border; pixels beyond the horizon are skipped.</summary>
    private static PlaneRectMm? ProjectFrame(FacetPlaneMap map, int width, int height)
    {
        var points = new List<(double A, double B)>();
        for (var i = 0; i <= SamplesPerEdge; i++)
        {
            var t = (double)i / SamplesPerEdge;
            (double X, double Y)[] border = [(t * width, 0), (t * width, height), (0, t * height), (width, t * height)];
            foreach (var (x, y) in border)
            {
                var p = map.ImageToPlaneMm(x, y);
                if (double.IsFinite(p.A) && double.IsFinite(p.B))
                {
                    points.Add(p);
                }
            }
        }

        return PlaneRectMm.Bounds(points);
    }

    private static PlaneRectMm MarkerBounds(FacetPlaneMap map, WallGeometryDocument document)
    {
        var corners = map.MarkerIds
            .Select(document.FindMarker)
            .Where(m => m is not null)
            .SelectMany(m => m!.CornersPlaneMm)
            .Select(c => (c[0], c[1]));
        return PlaneRectMm.Bounds(corners) ?? default;
    }
}
