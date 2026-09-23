using Blocwerk.Core.Abstractions;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// Turns one photo's accepted markers into per-facet pixel ↔ plane-mm homographies. With a
/// <see cref="WallGeometryDocument"/>: markers are grouped by facet; ≥2 markers → robust fit over
/// all their corners, exactly 1 → exact 4-point fit (see <see cref="PlaneMappingMode"/>). Without
/// one: <see cref="MapLocal(IReadOnlyList{DetectedMarker}, double)"/> gives each marker its own local square frame.
/// </summary>
public static class MarkerPlaneMapper
{
    private const double MinThresholdPx = 3.0;
    private const double ThresholdSideFraction = 0.1;

    /// <summary>Maps every facet the photo shows markers of.</summary>
    public static MarkerPlaneMapping Map(IReadOnlyList<DetectedMarker> markers, WallGeometryDocument document)
    {
        var unknown = new List<int>();
        var outliers = new List<int>();
        var known = new List<(DetectedMarker Detected, WallGeometryMarker Placed)>();
        foreach (var marker in markers)
        {
            var placed = document.FindMarker(marker.Id);
            if (placed is null || placed.CornersPlaneMm.Count != 4)
            {
                unknown.Add(marker.Id);
            }
            else
            {
                known.Add((marker, placed));
            }
        }

        var facets = new List<FacetPlaneMap>();
        foreach (var group in known.GroupBy(k => k.Placed.Facet).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var map = MapFacet(group.ToList(), outliers);
            if (map is not null)
            {
                facets.Add(map);
            }
        }

        return new MarkerPlaneMapping { Facets = facets, UnknownMarkerIds = unknown, OutlierMarkerIds = outliers };
    }

    /// <summary>
    /// Fallback without a geometry document: one <see cref="FacetPlaneMap"/> per marker, in that
    /// marker's own frame (BL corner at the origin, a right, b up, side = <paramref name="markerSizeMm"/>).
    /// Every map has <see cref="FacetPlaneMap.IsLocalFrame"/> = true. Legacy ids (<c>segment*6+role</c>).
    /// </summary>
    public static IReadOnlyList<FacetPlaneMap> MapLocal(IReadOnlyList<DetectedMarker> markers, double markerSizeMm = 125.0) =>
        MapLocal(markers, WallMarkerLayout.Legacy(markerSizeMm));

    /// <summary>
    /// As <see cref="MapLocal(IReadOnlyList{DetectedMarker}, double)"/>, each marker at its own printed size
    /// and segment from <paramref name="layout"/>; a marker of unknown size gets no frame.
    /// </summary>
    public static IReadOnlyList<FacetPlaneMap> MapLocal(IReadOnlyList<DetectedMarker> markers, WallMarkerLayout layout)
    {
        var maps = new List<FacetPlaneMap>();
        foreach (var marker in markers.OrderBy(m => m.Id))
        {
            if (layout.SizeOf(marker.Id) is not { } s)
            {
                continue;
            }

            double[][] square = [[0, s], [s, s], [s, 0], [0, 0]];
            var pairs = Pairs(marker, square);
            var toImage = PlaneHomography.Fit(pairs);
            var toPlane = toImage?.Inverse();
            if (toImage is null || toPlane is null)
            {
                continue;
            }

            var info = new FacetPlaneMapInfo(
                layout.SegmentOf(marker.Id) ?? -1, null, true, [marker.Id], Rms(toImage, pairs), 4, 4, marker.CenterPx);
            maps.Add(new FacetPlaneMap(toImage, toPlane, info));
        }

        return maps;
    }

    private static FacetPlaneMap? MapFacet(
        List<(DetectedMarker Detected, WallGeometryMarker Placed)> group,
        List<int> outliers)
    {
        // Reconstructed corners (e.g. id 1, cut off by the frame) only when nothing better exists.
        var observed = group.Where(g => !g.Detected.Synthetic && !g.Placed.Synthetic).ToList();
        var used = observed.Count > 0 ? observed : group;

        var pairs = used.SelectMany(g => Pairs(g.Detected, g.Placed.CornersPlaneMm)).ToList();
        var threshold = Math.Max(MinThresholdPx, ThresholdSideFraction * Median(used.Select(g => g.Detected.SidePx)));
        var fit = used.Count == 1
            ? ExactFit(pairs)
            : RobustHomographyFitter.Fit(pairs, 4, threshold);
        var toPlane = fit?.Homography.Inverse();
        if (fit is null || toPlane is null)
        {
            return null;
        }

        var supporting = new List<int>();
        for (var i = 0; i < used.Count; i++)
        {
            // A marker supports the fit when at least 3 of its 4 corners are inliers.
            var inlierCorners = fit.Inliers.Skip(i * 4).Take(4).Count(b => b);
            (inlierCorners >= 3 ? supporting : outliers).Add(used[i].Detected.Id);
        }

        var inlierPairs = pairs.Where((_, i) => fit.Inliers[i]).ToList();
        if (inlierPairs.Count < 4)
        {
            return null;
        }

        var reference = new MarkerPoint(inlierPairs.Average(p => p.DstX), inlierPairs.Average(p => p.DstY));
        var info = new FacetPlaneMapInfo(
            used[0].Placed.Segment,
            used[0].Placed.Facet,
            false,
            supporting,
            Rms(fit.Homography, inlierPairs),
            inlierPairs.Count,
            pairs.Count,
            reference);
        return new FacetPlaneMap(fit.Homography, toPlane, info);
    }

    private static RobustHomographyFit? ExactFit(List<PointCorrespondence> pairs)
    {
        var h = PlaneHomography.Fit(pairs);
        return h is null ? null : new RobustHomographyFit(h, [.. pairs.Select(_ => true)]);
    }

    private static List<PointCorrespondence> Pairs(DetectedMarker marker, IReadOnlyList<double[]> planeCorners)
    {
        var pairs = new List<PointCorrespondence>(4);
        for (var i = 0; i < 4; i++)
        {
            var px = marker.CornersPx[i];
            pairs.Add(new PointCorrespondence(planeCorners[i][0], planeCorners[i][1], px.X, px.Y));
        }

        return pairs;
    }

    private static double Rms(PlaneHomography h, IReadOnlyList<PointCorrespondence> pairs)
    {
        if (pairs.Count == 0)
        {
            return double.NaN;
        }

        return Math.Sqrt(pairs.Average(p => RobustHomographyFitter.ErrorSq(h, p)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
