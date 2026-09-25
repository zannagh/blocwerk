// <copyright file="CoverageAdviceWriter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Turns the coverage ratings into the "Next capture: what to add" list: volume faces first (the usual gaps), then
/// weak areas of the facets, then markers, then the video. Plain words, one action per line.
/// </summary>
public static class CoverageAdviceWriter
{
    /// <summary>Most lines about weak facet areas.</summary>
    public const int MaxSurfaceLines = 5;

    /// <summary>A weak area smaller than this share of its facet (and fewer than 3 cells) is not worth a line.</summary>
    private const double MinAreaShare = 0.03;

    /// <summary>A weak area covering this share of its facet is "all of" it.</summary>
    private const double WholeShare = 0.6;

    /// <summary>The whole list.</summary>
    /// <param name="facets">The rated facets.</param>
    /// <param name="markers">The facets' marker coverage, by facet id.</param>
    /// <param name="volumes">The rated volumes.</param>
    /// <param name="video">The video against the recipe.</param>
    /// <returns>The lines, most important first.</returns>
    public static IReadOnlyList<CoverageAdvice> Write(
        IReadOnlyList<RatedFacet> facets,
        IReadOnlyDictionary<string, MarkerCoverage> markers,
        IReadOnlyList<VolumeCoverage> volumes,
        VideoCoverage video)
    {
        var lines = new List<CoverageAdvice>();
        lines.AddRange(VolumeLines(facets, volumes));
        lines.AddRange(facets.SelectMany(SurfaceLines).OrderByDescending(l => l.Cells).Take(MaxSurfaceLines).Select(l => l.Advice));
        lines.AddRange(facets.SelectMany(f => markers.TryGetValue(f.Facet.Id, out var m) ? MarkerAdvice.Lines(f.Facet, m) : []));
        lines.AddRange(VideoAdvice.Lines(video));
        return lines;
    }

    private static IEnumerable<CoverageAdvice> VolumeLines(IReadOnlyList<RatedFacet> facets, IReadOnlyList<VolumeCoverage> volumes)
    {
        var weak = volumes
            .SelectMany(v => v.Faces.Where(f => f.Status != CoverageCellStatus.Good).Select(f => (Volume: v, f.Face)))
            .GroupBy(x => (x.Volume.FacetId, x.Face));
        foreach (var group in weak.OrderBy(g => g.Key.Face == VolumeFace.Underside ? 0 : 1).ThenByDescending(g => g.Count()))
        {
            if (facets.FirstOrDefault(f => f.Facet.Id == group.Key.FacetId)?.Facet is not { } facet)
            {
                continue;
            }

            var numbers = group.Select(x => x.Volume.Index).Order().ToList();
            var sides = group.Select(x => CoverageWhere.Horizontal(facet.Region, Centre(x.Volume))).Distinct().ToList();
            var place = sides.Count == 1 && sides[0] is { } side ? $"on the {side} of" : "on";
            var (noun, how) = FaceWords(group.Key.Face, numbers.Count > 1);
            var subject = numbers.Count == 1
                ? $"the {noun} of volume {numbers[0]} ({place} {CoverageWhere.Facet(facet.Name)})"
                : $"the {noun} of the {numbers.Count} volumes {place} {CoverageWhere.Facet(facet.Name)} ({CoverageWhere.Volumes(numbers)})";
            yield return new CoverageAdvice("volume", $"Shoot {subject} {how}", facet.Id);
        }
    }

    private static (string Noun, string How) FaceWords(VolumeFace face, bool plural) => face switch
    {
        VolumeFace.Underside => (plural ? "undersides" : "underside", "from below"),
        VolumeFace.TopSide => (plural ? "top sides" : "top side", "from above"),
        VolumeFace.LeftSide => (plural ? "left sides" : "left side", "from the left"),
        VolumeFace.RightSide => (plural ? "right sides" : "right side", "from the right"),
        _ => (plural ? "fronts" : "front", "face-on and from closer"),
    };

    private static double Centre(VolumeCoverage v) => v.Footprint.Count == 0 ? 0 : v.Footprint.Average(p => p[0]);

    /// <summary>The weak areas of a facet: per weakness its largest connected patch of cells.</summary>
    private static IEnumerable<(int Cells, CoverageAdvice Advice)> SurfaceLines(RatedFacet rated)
    {
        var total = rated.Status.Count(s => s != CoverageCellStatus.Hidden);
        CoverageCellStatus[] kinds = [CoverageCellStatus.Never, CoverageCellStatus.Grazing, CoverageCellStatus.FewDirections, CoverageCellStatus.LowResolution];
        foreach (var kind in kinds)
        {
            var mask = rated.Status.Select(s => s == kind).ToArray();
            var (labels, count) = rated.Grid.Label(mask);
            if (count == 0)
            {
                continue;
            }

            var patch = Enumerable.Range(1, count).Select(l => Enumerable.Range(0, labels.Length).Where(k => labels[k] == l).ToList()).MaxBy(p => p.Count)!;
            if (patch.Count < 3 && patch.Count < total * MinAreaShare)
            {
                continue;
            }

            yield return (patch.Count, new CoverageAdvice("surface", SurfaceText(rated, kind, patch, patch.Count >= total * WholeShare), rated.Facet.Id));
        }
    }

    private static string SurfaceText(RatedFacet rated, CoverageCellStatus kind, List<int> patch, bool whole)
    {
        var centres = patch.Select(rated.Grid.Centre).ToList();
        var facet = CoverageWhere.Facet(rated.Facet.Name);
        var place = whole ? $"all of {facet}" : $"{CoverageWhere.Describe(rated.Facet.Region, centres.Average(c => c.A), centres.Average(c => c.B))} of {facet}";
        var views = patch.Select(k => rated.Views[k]).ToList();
        return kind switch
        {
            CoverageCellStatus.Never => $"Photograph {place}: no photo shows it",
            CoverageCellStatus.Grazing => $"Shoot {place} more face-on: it is only seen at a steep angle",
            CoverageCellStatus.FewDirections => string.Create(
                CultureInfo.InvariantCulture, $"Shoot {place} from more directions: it is seen from only {views.Max(v => v.Directions)}"),
            _ => string.Create(
                CultureInfo.InvariantCulture, $"Get closer to {place}: it is only seen from far away ({views.Min(v => v.BestMmPerPx):F1} mm per pixel)"),
        };
    }
}
