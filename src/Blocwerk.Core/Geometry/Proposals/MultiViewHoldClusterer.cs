// <copyright file="MultiViewHoldClusterer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>
/// Groups detections of one physical hold across photos. Greedy, most confident detection first: it takes, from
/// every OTHER photo, the nearest unused hit on the same facet within <see cref="LinkMm"/> and of similar size,
/// triangulates their rays, and drops the views whose detection the point does not reproject into (more than
/// <see cref="MaxReprojectionShare"/> of the box radius away), until the rest agree. A cluster is kept only when
/// ≥ minViews photos agree, their rays meet within <see cref="MaxResidualMm"/> and they see it from angles at
/// least <see cref="MinBaselineDeg"/> apart (a narrow baseline cannot tell a hold from the wall behind it).
/// Tuned on The Attic (2026-09-25): union-find chaining instead gave floating points between neighbouring holds.
/// </summary>
public static class MultiViewHoldClusterer
{
    /// <summary>Largest distance between two wall hits of one hold, mm (its own parallax across views included).</summary>
    public const double LinkMm = 45;

    /// <summary>Largest size ratio between two detections of one hold.</summary>
    public const double MaxSizeRatio = 2.2;

    /// <summary>A view agrees when the point reprojects within this share of its box radius.</summary>
    public const double MaxReprojectionShare = 0.35;

    /// <summary>Median distance of the point to its rays must stay below this, mm.</summary>
    public const double MaxResidualMm = 12;

    /// <summary>Widest angle between two of its rays must reach this, degrees.</summary>
    public const double MinBaselineDeg = 10;

    private const double MinRadiusPx = 6;
    private const int Rounds = 3;

    /// <summary>The clusters seen by at least <paramref name="minViews"/> agreeing photos.</summary>
    /// <param name="hits">All hits.</param>
    /// <param name="cameras">The photos' cameras by name.</param>
    /// <param name="minViews">Fewest photos.</param>
    /// <returns>The clusters.</returns>
    public static List<HoldCluster> Cluster(IReadOnlyList<SurfaceHit> hits, IReadOnlyDictionary<string, SolvedCamera> cameras, int minViews = 3)
    {
        var grid = new Dictionary<(string Facet, long I, long J), List<int>>();
        for (var i = 0; i < hits.Count; i++)
        {
            var key = (hits[i].FacetId, (long)Math.Floor(hits[i].A / LinkMm), (long)Math.Floor(hits[i].B / LinkMm));
            if (!grid.TryGetValue(key, out var list))
            {
                list = [];
                grid[key] = list;
            }

            list.Add(i);
        }

        var used = new bool[hits.Count];
        var result = new List<HoldCluster>();
        foreach (var seed in Enumerable.Range(0, hits.Count).OrderByDescending(i => hits[i].Detection.Confidence))
        {
            if (used[seed])
            {
                continue;
            }

            var members = Agreeing(Candidates(hits, grid, used, seed), seed, hits, cameras);
            if (members.Count >= minViews && Triangulate(members.Select(m => hits[m]).ToList()) is { } cluster
                && cluster.ResidualMm <= MaxResidualMm && BaselineDeg(cluster.Hits) >= MinBaselineDeg)
            {
                members.ForEach(m => used[m] = true);
                result.Add(cluster);
            }
            else
            {
                used[seed] = true;
            }
        }

        return result;
    }

    /// <summary>The cluster's point: least squares over its rays, or the mean hit when they are (nearly) parallel.</summary>
    /// <param name="hits">One hit per photo.</param>
    /// <returns>The cluster.</returns>
    public static HoldCluster Triangulate(List<SurfaceHit> hits)
    {
        var point = RayMath.Nearest(hits) ?? RayMath.Mean(hits);
        var residual = hits.Select(h => RayMath.Distance(h, point)).Order().ElementAt(hits.Count / 2);
        return new HoldCluster(hits, point, Math.Round(residual, 1));
    }

    /// <summary>The seed plus, per other photo, its nearest unused compatible hit.</summary>
    private static List<int> Candidates(IReadOnlyList<SurfaceHit> hits, Dictionary<(string Facet, long I, long J), List<int>> grid, bool[] used, int seed)
    {
        var s = hits[seed];
        var (i0, j0) = ((long)Math.Floor(s.A / LinkMm), (long)Math.Floor(s.B / LinkMm));
        var best = new Dictionary<string, (double D, int Index)>();
        for (var di = -1; di <= 1; di++)
        {
            for (var dj = -1; dj <= 1; dj++)
            {
                foreach (var j in grid.GetValueOrDefault((s.FacetId, i0 + di, j0 + dj)) ?? [])
                {
                    var q = hits[j];
                    var d = RayMath.Length(q.World, s.World);
                    var ratio = Math.Max(q.SizeMm, s.SizeMm) / Math.Max(1, Math.Min(q.SizeMm, s.SizeMm));
                    if (used[j] || q.Detection.Photo == s.Detection.Photo || d > LinkMm || ratio > MaxSizeRatio)
                    {
                        continue;
                    }

                    if (!best.TryGetValue(q.Detection.Photo, out var b) || d < b.D)
                    {
                        best[q.Detection.Photo] = (d, j);
                    }
                }
            }
        }

        return [seed, .. best.Values.Select(b => b.Index)];
    }

    /// <summary>Drops the views the triangulated point does not reproject into, a few rounds; the seed stays.</summary>
    private static List<int> Agreeing(List<int> members, int seed, IReadOnlyList<SurfaceHit> hits, IReadOnlyDictionary<string, SolvedCamera> cameras)
    {
        for (var round = 0; round < Rounds && members.Count >= 2; round++)
        {
            var point = Triangulate(members.Select(m => hits[m]).ToList()).Point;
            var keep = members.Where(m => m == seed || Agrees(hits[m], point, cameras)).ToList();
            if (keep.Count == members.Count)
            {
                break;
            }

            members = keep;
        }

        return members;
    }

    private static bool Agrees(SurfaceHit h, double[] point, IReadOnlyDictionary<string, SolvedCamera> cameras)
    {
        if (!cameras.TryGetValue(h.Detection.Photo, out var camera) || camera.Project(point) is not { } px)
        {
            return false;
        }

        var error = Math.Sqrt(((px.X - h.Detection.Px) * (px.X - h.Detection.Px)) + ((px.Y - h.Detection.Py) * (px.Y - h.Detection.Py)));
        return error <= MaxReprojectionShare * Math.Max(MinRadiusPx, h.Detection.RadiusPx);
    }

    private static double BaselineDeg(IReadOnlyList<SurfaceHit> hits)
    {
        var minCos = 1.0;
        foreach (var a in hits)
        {
            foreach (var b in hits)
            {
                minCos = Math.Min(minCos, RayMath.Dot(a.Direction, b.Direction));
            }
        }

        return Math.Acos(Math.Clamp(minCos, -1, 1)) * 180 / Math.PI;
    }
}
