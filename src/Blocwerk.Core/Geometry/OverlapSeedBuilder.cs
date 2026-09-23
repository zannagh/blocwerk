using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Geometry;

/// <summary>One photo's markers in its RAW pixel frame (EXIF orientation ignored), plus that frame's size.</summary>
/// <param name="Markers">Accepted markers, pixel corners in the raw frame.</param>
/// <param name="Width">Raw frame width in pixels.</param>
/// <param name="Height">Raw frame height in pixels.</param>
public sealed record SeedPhoto(IReadOnlyList<DetectedMarker> Markers, int Width, int Height);

/// <summary>A left hold the seed should know about: its normalized raw-frame centre and measured facet.</summary>
/// <param name="X">Normalized centre x (0..1).</param>
/// <param name="Y">Normalized centre y (0..1).</param>
/// <param name="FacetId">The facet the hold was measured on, or null.</param>
public readonly record struct SeedHold(double X, double Y, string? FacetId);

/// <summary>
/// Builds a <see cref="HoldOverlapSeed"/> (left photo → right photo homography) from the markers of two
/// photos. With a wall model the homography is composed through the facet plane carrying most of the
/// holds in play (photo A → plane → photo B, each side from its own markers, so the photos need not
/// share a marker); without one it is fitted straight from the corners of markers visible in both.
/// Pure: no database, no images.
/// </summary>
public static class OverlapSeedBuilder
{
    private const double MinThresholdPx = 3.0;
    private const double ThresholdSideFraction = 0.1;

    /// <summary>Builds the seed, or null when the markers do not constrain the pair.</summary>
    /// <param name="left">Left photo markers.</param>
    /// <param name="right">Right photo markers.</param>
    /// <param name="document">The active wall model, or null.</param>
    /// <param name="leftHolds">The left holds in play (facet choice and plane-induced priors).</param>
    /// <returns>The seed without anchors, or null.</returns>
    public static HoldOverlapSeed? Build(
        SeedPhoto left, SeedPhoto right, WallGeometryDocument? document, IReadOnlyList<SeedHold> leftHolds)
    {
        if (left.Width <= 0 || left.Height <= 0 || right.Width <= 0 || right.Height <= 0)
        {
            return null;
        }

        var measured = SharedCorners(left, right);
        var seed = document is null ? null : FromFacets(left, right, document, leftHolds, measured);
        seed ??= FromSharedMarkers(left, right, measured);
        return seed;
    }

    /// <summary>Corner pairs (pixels) of every marker id both photos show, observed corners preferred.</summary>
    internal static List<PointCorrespondence> SharedCorners(SeedPhoto left, SeedPhoto right)
    {
        var rightById = right.Markers.ToDictionary(m => m.Id);
        var shared = left.Markers
            .Where(m => rightById.ContainsKey(m.Id))
            .OrderBy(m => m.Id)
            .Select(m => (L: m, R: rightById[m.Id]))
            .ToList();
        var observed = shared.Where(s => !s.L.Synthetic && !s.R.Synthetic).ToList();
        var used = observed.Count > 0 ? observed : shared;

        var pairs = new List<PointCorrespondence>(used.Count * 4);
        foreach (var (l, r) in used)
        {
            for (var c = 0; c < 4; c++)
            {
                pairs.Add(new PointCorrespondence(l.CornersPx[c].X, l.CornersPx[c].Y, r.CornersPx[c].X, r.CornersPx[c].Y));
            }
        }

        return pairs;
    }

    private static HoldOverlapSeed? FromSharedMarkers(SeedPhoto left, SeedPhoto right, List<PointCorrespondence> pairs)
    {
        var markers = pairs.Count / 4;
        if (markers == 0)
        {
            return null;
        }

        var threshold = Math.Max(MinThresholdPx, ThresholdSideFraction * MedianSide(right.Markers));
        PlaneHomography? h;
        if (markers == 1)
        {
            h = PlaneHomography.Fit(pairs);
        }
        else
        {
            h = RobustHomographyFitter.Fit(pairs, 4, threshold)?.Homography;
        }

        if (h is null)
        {
            return null;
        }

        return new HoldOverlapSeed
        {
            Homography = ToNormalized(h, left, right),
            Source = HoldOverlapSeedSource.SharedMarkers,
            MarkerCount = markers,
            FitRmsPx = Rms(h, pairs),
            MeasuredPairs = Normalize(pairs, left, right),
        };
    }

    private static HoldOverlapSeed? FromFacets(
        SeedPhoto left,
        SeedPhoto right,
        WallGeometryDocument document,
        IReadOnlyList<SeedHold> leftHolds,
        List<PointCorrespondence> measured)
    {
        var mapL = MarkerPlaneMapper.Map(left.Markers, document);
        var mapR = MarkerPlaneMapper.Map(right.Markers, document);
        var composed = new Dictionary<string, (PlaneHomography H, int Markers)>(StringComparer.Ordinal);
        foreach (var l in mapL.Facets)
        {
            var r = l.FacetId is null ? null : mapR.Facet(l.FacetId);
            if (r is not null)
            {
                composed[l.FacetId!] = (l.ImageToPlane.Then(r.PlaneToImage), Math.Min(l.MarkerCount, r.MarkerCount));
            }
        }

        if (composed.Count == 0)
        {
            return null;
        }

        var holdCounts = leftHolds
            .Where(h => h.FacetId is not null && composed.ContainsKey(h.FacetId))
            .GroupBy(h => h.FacetId!)
            .ToDictionary(g => g.Key, g => g.Count());
        var chosen = composed
            .OrderByDescending(kv => holdCounts.GetValueOrDefault(kv.Key))
            .ThenByDescending(kv => kv.Value.Markers)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .First();

        return new HoldOverlapSeed
        {
            Homography = ToNormalized(chosen.Value.H, left, right),
            Source = HoldOverlapSeedSource.GeometryFacet,
            MarkerCount = chosen.Value.Markers,
            FacetId = chosen.Key,
            FitRmsPx = measured.Count == 0 ? double.NaN : Rms(chosen.Value.H, measured),
            MeasuredPairs = Normalize(measured, left, right),
            PriorPairs = Priors(left, right, leftHolds, mapL, composed),
        };
    }

    /// <summary>Each facet-tagged left hold's plane-induced position in the right photo (inside it only).</summary>
    private static List<HoldOverlapPointPair> Priors(
        SeedPhoto left,
        SeedPhoto right,
        IReadOnlyList<SeedHold> leftHolds,
        MarkerPlaneMapping mapL,
        Dictionary<string, (PlaneHomography H, int Markers)> composed)
    {
        var priors = new List<HoldOverlapPointPair>();
        foreach (var hold in leftHolds)
        {
            if (hold.FacetId is null || !composed.TryGetValue(hold.FacetId, out var facet))
            {
                continue;
            }

            double px = hold.X * left.Width, py = hold.Y * left.Height;
            if (double.IsNaN(mapL.Facet(hold.FacetId)!.ImageToPlaneMm(px, py).A))
            {
                continue;
            }

            var (x, y) = facet.H.Apply(px, py);
            double nx = x / right.Width, ny = y / right.Height;
            if (double.IsFinite(nx) && double.IsFinite(ny) && nx is >= 0 and <= 1 && ny is >= 0 and <= 1)
            {
                priors.Add(new HoldOverlapPointPair(hold.X, hold.Y, nx, ny));
            }
        }

        return priors;
    }

    /// <summary>Re-expresses a pixel homography between normalized raw coordinates.</summary>
    private static double[] ToNormalized(PlaneHomography px, SeedPhoto left, SeedPhoto right)
    {
        var toPx = PlaneHomography.FromCoefficients([left.Width, 0, 0, 0, left.Height, 0, 0, 0, 1]);
        var fromPx = PlaneHomography.FromCoefficients([1.0 / right.Width, 0, 0, 0, 1.0 / right.Height, 0, 0, 0, 1]);
        return toPx.Then(px).Then(fromPx).Coefficients;
    }

    private static List<HoldOverlapPointPair> Normalize(List<PointCorrespondence> pairs, SeedPhoto left, SeedPhoto right) =>
        pairs.Select(p => new HoldOverlapPointPair(
            p.SrcX / left.Width, p.SrcY / left.Height, p.DstX / right.Width, p.DstY / right.Height)).ToList();

    private static double Rms(PlaneHomography h, List<PointCorrespondence> pairs) =>
        pairs.Count == 0 ? double.NaN : Math.Sqrt(pairs.Average(p => RobustHomographyFitter.ErrorSq(h, p)));

    private static double MedianSide(IReadOnlyList<DetectedMarker> markers)
    {
        var sorted = markers.Select(m => m.SidePx).OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
