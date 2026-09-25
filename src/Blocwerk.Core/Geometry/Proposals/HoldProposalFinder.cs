// <copyright file="HoldProposalFinder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>
/// Multi-view hold inventory: every capture photo's detections are lifted onto the wall
/// (<see cref="WallSurfaceCaster"/>), clustered across photos (<see cref="MultiViewHoldClusterer"/>), and every
/// cluster that is not an existing hold (<see cref="KnownHoldReference"/>) and not near an earlier reviewed
/// proposal becomes a candidate. Nothing here writes anything: the candidates are PROPOSALS for a wall admin.
/// </summary>
public static class HoldProposalFinder
{
    /// <summary>How far out along its panel ray a known hold may stand, mm (big holds on volumes included).</summary>
    public const double KnownRayMm = 300;

    /// <summary>A known hold matches a cluster within this, mm, plus a share of its size.</summary>
    public const double MatchMm = 40;

    /// <summary>A candidate this near an earlier accepted or rejected proposal is not proposed again, mm.</summary>
    public const double SuppressMm = 50;

    /// <summary>Smallest proposal, mm: below are bolt holes, T-nuts and knots, which the detector also finds.</summary>
    public const double MinSizeMm = 30;

    /// <summary>
    /// A hold stands out of the surface it sits on, so its triangulated centre does too; a detection of something
    /// flat (a bolt hole, a stain, a printed marker) triangulates onto the surface. Mm above the facet or volume.
    /// </summary>
    public const double MinReliefMm = 10;

    /// <summary>Fewest photos per proposal: two photos agree on some wood grain by chance.</summary>
    public const int DefaultMinViews = 3;

    /// <summary>The candidates.</summary>
    /// <param name="cameras">The capture cameras by name.</param>
    /// <param name="detections">All detections of all photos.</param>
    /// <param name="facets">The model's facets with their volumes.</param>
    /// <param name="known">The existing holds.</param>
    /// <param name="reviewed">World points of earlier reviewed proposals (never proposed again).</param>
    /// <param name="minViews">Fewest photos per candidate.</param>
    /// <returns>The candidates (most photos first) and how many multi-view clusters were found.</returns>
    public static (List<HoldProposalCandidate> Candidates, int Clusters) Find(
        IReadOnlyDictionary<string, SolvedCamera> cameras,
        IEnumerable<CaptureDetection> detections,
        IReadOnlyList<CastFacet> facets,
        IReadOnlyList<KnownHoldReference> known,
        IReadOnlyList<double[]> reviewed,
        int minViews = DefaultMinViews)
    {
        var hits = detections
            .Select(d => cameras.TryGetValue(d.Photo, out var c) ? WallSurfaceCaster.Cast(c, d, facets) : null)
            .OfType<SurfaceHit>()
            .ToList();
        var byId = facets.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var result = new List<HoldProposalCandidate>();
        var clusters = MultiViewHoldClusterer.Cluster(hits, cameras, minViews);
        foreach (var cluster in clusters)
        {
            if (Candidate(cluster, byId, known, reviewed) is { } candidate)
            {
                result.Add(candidate);
            }
        }

        return (result.OrderByDescending(c => c.Views).ThenByDescending(c => c.Confidence).ToList(), clusters.Count);
    }

    private static HoldProposalCandidate? Candidate(
        HoldCluster cluster, Dictionary<string, CastFacet> facets, IReadOnlyList<KnownHoldReference> known, IReadOnlyList<double[]> reviewed)
    {
        var p = cluster.Point;
        var size = cluster.Hits.Select(h => h.SizeMm).Order().ElementAt(cluster.Hits.Count / 2);
        if (size < MinSizeMm || known.Any(k => k.DistanceTo(p) <= k.ToleranceMm + (0.25 * size)) || reviewed.Any(r => Distance(r, p) <= SuppressMm))
        {
            return null;
        }

        var facet = facets[cluster.Hits.GroupBy(h => h.FacetId).MaxBy(g => g.Count())!.Key];
        var (a, b, h) = FacetCloud.Local(facet.Frame, p[0], p[1], p[2]);
        var top = facet.Volumes.Select(v => v.HeightAt(a, b)).DefaultIfEmpty(0).Max();
        var e = facet.Extent;
        if (a < e.AMin || a > e.AMax || b < e.BMin || b > e.BMax || h < top + MinReliefMm || h > top + 150)
        {
            return null;
        }

        var best = cluster.Hits.MaxBy(x => x.CosView * x.Detection.Confidence)!;
        return new HoldProposalCandidate(
            facet.Id, Math.Round(a, 1), Math.Round(b, 1), Math.Round(h, 1), p, Math.Round(size, 1), cluster.Views,
            Math.Round(cluster.Hits.Average(x => x.Detection.Confidence), 3), cluster.ResidualMm, best.Detection, cluster.Hits.Select(x => x.Detection).ToList());
    }

    private static double Distance(double[] x, double[] y) =>
        Math.Sqrt(((x[0] - y[0]) * (x[0] - y[0])) + ((x[1] - y[1]) * (x[1] - y[1])) + ((x[2] - y[2]) * (x[2] - y[2])));
}
