// <copyright file="SimilarHoldAssigner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.HoldMoves;

/// <summary>What the assigner knows about a hold, old or new.</summary>
/// <param name="Id">The hold.</param>
/// <param name="Color">The colour key, when set.</param>
/// <param name="Fingerprint">The appearance fingerprint (colour, shape), when measured.</param>
/// <param name="WidthMm">The measured long side, mm.</param>
/// <param name="DepthMm">How far it stands out of the wall, mm, when measured.</param>
public sealed record SimilarHold(Guid Id, string? Color, HoldFingerprint? Fingerprint, double? WidthMm, double? DepthMm);

/// <summary>One old hold paired with the new detection it most likely is.</summary>
/// <param name="OldId">The old hold.</param>
/// <param name="NewId">The new detection.</param>
/// <param name="DistanceMm">How far apart they are, when known.</param>
public sealed record HoldAssignment(Guid OldId, Guid NewId, double? DistanceMm);

/// <summary>
/// "There are five similar holds on our wall": holds that look alike (colour, shape, size, depth) form a group, and
/// inside a group the old holds are assigned to the new detections so the TOTAL movement is smallest (minimum-cost
/// assignment, Hungarian). When one of five look-alikes shows up elsewhere, that one is the mover and the other four
/// stayed. Without a distance a pair is never assigned. Pure and deterministic: groups and ids are ordered, ties break
/// by id.
/// </summary>
public static class SimilarHoldAssigner
{
    private const double Unknown = 1e9;
    private const double Pad = 1e7;

    /// <summary>Whether two holds look alike: nothing known about either contradicts it, and something positive says so.</summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <param name="o">The thresholds.</param>
    /// <returns>True for look-alikes.</returns>
    public static bool AreSimilar(SimilarHold a, SimilarHold b, HoldMoveOptions o)
    {
        var colourKnown = a.Color is not null && b.Color is not null;
        var sameColour = colourKnown && string.Equals(a.Color, b.Color, StringComparison.OrdinalIgnoreCase);
        var shape = a.Fingerprint is not null && b.Fingerprint is not null ? HoldFingerprint.Similarity(a.Fingerprint, b.Fingerprint) : (double?)null;
        var contradicts = (colourKnown && !sameColour)
            || (shape is { } s && s < o.SimilarityMin)
            || (a.WidthMm is { } wa && b.WidthMm is { } wb && Math.Abs(wa - wb) > o.SizeTolerance * Math.Max(wa, wb))
            || (a.DepthMm is { } da && b.DepthMm is { } db && Math.Abs(da - db) > o.DepthToleranceMm);
        return !contradicts && (sameColour || shape is not null);
    }

    /// <summary>Assigns the old holds to the new ones group by group, by least total movement.</summary>
    /// <param name="olds">Old holds with no positional counterpart.</param>
    /// <param name="news">New detections that matched no old hold.</param>
    /// <param name="distanceMm">The distance between an old and a new hold, or null when unknown.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The assignments, ordered by old hold id.</returns>
    public static IReadOnlyList<HoldAssignment> Assign(
        IReadOnlyList<SimilarHold> olds,
        IReadOnlyList<SimilarHold> news,
        Func<Guid, Guid, double?> distanceMm,
        HoldMoveOptions options)
    {
        var all = olds.Select(h => (H: h, IsOld: true)).Concat(news.Select(h => (H: h, IsOld: false))).OrderBy(x => x.H.Id).ToList();
        var groups = Group(all.Select(x => x.H).ToList(), options);
        var result = new List<HoldAssignment>();
        foreach (var members in groups)
        {
            var go = olds.Where(o => members.Contains(o.Id)).OrderBy(o => o.Id).ToList();
            var gn = news.Where(n => members.Contains(n.Id)).OrderBy(n => n.Id).ToList();
            if (go.Count > 0 && gn.Count > 0)
            {
                result.AddRange(AssignGroup(go, gn, distanceMm));
            }
        }

        return result.OrderBy(a => a.OldId).ToList();
    }

    private static List<HashSet<Guid>> Group(IReadOnlyList<SimilarHold> holds, HoldMoveOptions o)
    {
        var parent = holds.Select((_, i) => i).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < holds.Count; i++)
        {
            for (var j = i + 1; j < holds.Count; j++)
            {
                if (AreSimilar(holds[i], holds[j], o))
                {
                    parent[Find(j)] = Find(i);
                }
            }
        }

        return holds.Select((h, i) => (h.Id, Root: Find(i))).GroupBy(x => x.Root)
            .Select(g => g.Select(x => x.Id).ToHashSet()).ToList();
    }

    private static List<HoldAssignment> AssignGroup(
        List<SimilarHold> olds, List<SimilarHold> news, Func<Guid, Guid, double?> distanceMm)
    {
        var n = Math.Max(olds.Count, news.Count);
        var cost = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                cost[i, j] = i >= olds.Count || j >= news.Count ? Pad : distanceMm(olds[i].Id, news[j].Id) ?? Unknown;
            }
        }

        var column = Hungarian(cost, n);
        var assigned = new List<HoldAssignment>();
        for (var i = 0; i < olds.Count; i++)
        {
            var j = column[i];
            if (j < news.Count && cost[i, j] < Unknown)
            {
                assigned.Add(new HoldAssignment(olds[i].Id, news[j].Id, cost[i, j]));
            }
        }

        return assigned;
    }

    /// <summary>Minimum-cost assignment on a square matrix (potentials method); returns the column chosen per row.</summary>
    private static int[] Hungarian(double[,] a, int n)
    {
        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                var delta = double.PositiveInfinity;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    var cur = a[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        (minv[j], way[j]) = (cur, j0);
                    }

                    if (minv[j] < delta)
                    {
                        (delta, j1) = (minv[j], j);
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        (u[p[j]], v[j]) = (u[p[j]] + delta, v[j] - delta);
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var column = new int[n];
        for (var j = 1; j <= n; j++)
        {
            column[p[j] - 1] = j - 1;
        }

        return column;
    }
}
