// <copyright file="MarkerCoverageRater.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Per facet: the markers the solve placed on it and in how many photos each was seen. A facet with fewer than 3
/// markers, markers seen in fewer than 3 photos, or markers bunched on a large facet is poorly pinned down; for it the
/// report suggests where to add markers (the facet cells farthest from the existing ones) and how big to print them
/// (<see cref="MarkerSizing"/> for the facet's angles and the capture's own cameras).
/// </summary>
public static class MarkerCoverageRater
{
    /// <summary>Fewer markers than this on a facet is too few.</summary>
    public const int MinMarkers = 3;

    /// <summary>A marker seen in fewer photos than this is weak.</summary>
    public const int MinPhotos = 3;

    /// <summary>Markers spanning less than this share of a facet side longer than <see cref="SpreadSideMm"/> are bunched.</summary>
    public const double MinSpan = 0.5;

    /// <summary>Only a facet side longer than this can have its markers bunched, mm.</summary>
    public const double SpreadSideMm = 800;

    /// <summary>No markers and nothing to suggest (a model solved from photo features).</summary>
    public static MarkerCoverage None { get; } = new([], 0, false, false, [], null);

    /// <summary>Photo distance assumed without any camera seeing the facet, mm.</summary>
    private const double DefaultDistanceMm = 2500;

    /// <summary>Rates a facet's markers.</summary>
    /// <param name="doc">The model.</param>
    /// <param name="rated">The facet's rated grid.</param>
    /// <param name="cameras">The posed cameras (for the size hint).</param>
    /// <returns>The marker coverage.</returns>
    public static MarkerCoverage Rate(WallGeometryDocument doc, RatedFacet rated, IReadOnlyList<CoverageCamera> cameras)
    {
        var facet = rated.Facet;
        var markers = doc.Markers
            .Where(m => m.Facet == facet.Id && m.CornersPlaneMm.Count == 4 && m.CornersPlaneMm.All(c => c.Length >= 2))
            .Select(m => new MarkerSeen(m.Id, m.Observations, m.CornersPlaneMm.Average(c => c[0]), m.CornersPlaneMm.Average(c => c[1])))
            .OrderBy(m => m.Id)
            .ToList();
        var r = facet.Region;
        var spanA = markers.Count < 2 ? 0 : Math.Min(1, (markers.Max(m => m.A) - markers.Min(m => m.A)) / Math.Max(1, r.Width));
        var spanB = markers.Count < 2 ? 0 : Math.Min(1, (markers.Max(m => m.B) - markers.Min(m => m.B)) / Math.Max(1, r.Height));
        var few = markers.Count < MinMarkers;
        var bunched = !few && ((r.Width > SpreadSideMm && spanA < MinSpan) || (r.Height > SpreadSideMm && spanB < MinSpan));
        var weak = markers.Where(m => m.Photos < MinPhotos).Select(m => m.Id).ToList();
        var add = few ? MinMarkers - markers.Count : bunched ? 2 : 0;
        var suggestion = add > 0 ? Suggest(rated, markers, add, SizeHint(facet, cameras)) : null;
        return new MarkerCoverage(markers, Math.Round(spanA * spanB, 3), few, bunched, weak, suggestion);
    }

    /// <summary>
    /// The printed size the marker sizing rules pick for a filler on this facet, photographed like this capture:
    /// the median camera's field of view and image size, at the median distance of the cameras that see the facet.
    /// </summary>
    /// <param name="facet">The facet.</param>
    /// <param name="cameras">The posed cameras.</param>
    /// <returns>The size, mm.</returns>
    public static double SizeHint(CoverageFacet facet, IReadOnlyList<CoverageCamera> cameras)
    {
        var r = facet.Region;
        var centre = facet.Frame.ToWorld((r.AMin + r.AMax) / 2, (r.BMin + r.BMax) / 2);
        var seeing = cameras.Where(c => c.InFrame(centre)).ToList();
        var pool = seeing.Count > 0 ? seeing : cameras;
        var distance = seeing.Count > 0 ? Median(seeing.Select(c => Distance(c.Centre, centre))) : DefaultDistanceMm;
        var cam = pool.Count > 0 ? pool.OrderBy(c => c.FocalPx).ElementAt(pool.Count / 2).Camera : null;
        var longEdge = cam is null ? 4032 : Math.Max(cam.Width, cam.Height);
        var hfov = cam is null ? 69.0 : 2 * Math.Atan(longEdge / (2 * cam.K[0])) * 180 / Math.PI;
        var segment = new PlanSegment(
            0, facet.Name, SegmentShape.Rectangle, r.Width, r.Height, TriangleCorner.BottomLeft, facet.OverhangDeg, facet.YawDeg, null);
        var photo = new PhotoSetup(distance, MarkerCameraPresets.Custom, hfov, longEdge);
        return MarkerSizing.PickSize(MarkerRole.Filler, segment, photo, MarkerGenerationOptions.Default, out _);
    }

    /// <summary>Farthest-point picks over the facet's cells (not under a volume), away from the existing markers.</summary>
    private static MarkerSuggestion Suggest(RatedFacet rated, List<MarkerSeen> markers, int count, double sizeMm)
    {
        var taken = markers.Select(m => (m.A, m.B)).ToList();
        var candidates = Enumerable.Range(0, rated.Status.Length)
            .Where(k => rated.Status[k] != CoverageCellStatus.Hidden)
            .Select(rated.Grid.Centre)
            .ToList();
        var picks = new List<double[]>();
        for (var i = 0; i < count && candidates.Count > 0; i++)
        {
            var best = candidates.OrderByDescending(c => taken.Count == 0 ? -Distance2(c, Centre(rated)) : taken.Min(t => Distance2(c, t))).First();
            picks.Add([Math.Round(best.A), Math.Round(best.B)]);
            taken.Add(best);
            candidates.Remove(best);
        }

        var where = picks.Count == 0 ? "anywhere" : CoverageWhere.Describe(rated.Facet.Region, picks.Average(p => p[0]), picks.Average(p => p[1]));
        return new MarkerSuggestion(picks.Count, sizeMm, picks, where);
    }

    private static (double A, double B) Centre(RatedFacet rated) =>
        ((rated.Facet.Region.AMin + rated.Facet.Region.AMax) / 2, (rated.Facet.Region.BMin + rated.Facet.Region.BMax) / 2);

    private static double Distance2((double A, double B) p, (double A, double B) q) => ((p.A - q.A) * (p.A - q.A)) + ((p.B - q.B) * (p.B - q.B));

    private static double Distance(double[] p, double[] q) =>
        Math.Sqrt(((p[0] - q[0]) * (p[0] - q[0])) + ((p[1] - q[1]) * (p[1] - q[1])) + ((p[2] - q[2]) * (p[2] - q[2])));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? DefaultDistanceMm : sorted[sorted.Count / 2];
    }
}
