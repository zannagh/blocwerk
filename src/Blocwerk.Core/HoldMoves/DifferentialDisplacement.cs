// <copyright file="DifferentialDisplacement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// The raw 3D displacement of a carried hold includes the registration error of the new photo, which is systematic and
/// regional (the same holds are off by the same amount, run after run). What cancels it is each hold's displacement
/// RELATIVE to its unmoved neighbourhood: the median displacement vector of the carried holds around it on the same facet is
/// subtracted, and what is left is the hold's own movement. The spread of the neighbours' own residuals says how much to
/// trust it. Pure and deterministic (ordered by id, ties by id).
/// </summary>
public static class DifferentialDisplacement
{
    private const int MaxNeighbours = 40;

    /// <summary>One carried pair: where the old hold was (facet, mm) and its raw displacement.</summary>
    /// <param name="Id">The old hold.</param>
    /// <param name="FacetId">The facet.</param>
    /// <param name="A">Old position across the facet, mm.</param>
    /// <param name="B">Old position up the facet, mm.</param>
    /// <param name="Da">Raw displacement across, mm.</param>
    /// <param name="Db">Raw displacement up, mm.</param>
    /// <param name="Dh">Raw displacement out of the wall, mm.</param>
    public sealed record Input(Guid Id, string FacetId, double A, double B, double Da, double Db, double Dh);

    /// <summary>A hold's displacement after the neighbourhood's was subtracted.</summary>
    /// <param name="ResidualMm">The remaining distance, mm.</param>
    /// <param name="SpreadMm">The typical residual of the neighbours themselves, mm.</param>
    /// <param name="Neighbours">How many neighbours were used.</param>
    public sealed record Result(double ResidualMm, double SpreadMm, int Neighbours);

    /// <summary>The differential displacement of every input that has enough neighbours.</summary>
    /// <param name="inputs">The carried pairs of one update.</param>
    /// <param name="options">The neighbourhood thresholds.</param>
    /// <returns>The result per hold id; holds with too few neighbours are missing.</returns>
    public static Dictionary<Guid, Result> Compute(IReadOnlyList<Input> inputs, HoldMoveOptions options)
    {
        var ordered = inputs.OrderBy(i => i.Id).ToList();
        var byFacet = ordered.GroupBy(i => i.FacetId).ToDictionary(g => g.Key, g => g.ToList());
        var results = new Dictionary<Guid, Result>();
        foreach (var hold in ordered)
        {
            var neighbours = byFacet[hold.FacetId]
                .Where(n => n.Id != hold.Id)
                .Select(n => (N: n, D: Math.Sqrt(Math.Pow(n.A - hold.A, 2) + Math.Pow(n.B - hold.B, 2))))
                .Where(x => x.D <= options.NeighbourRadiusMm)
                .OrderBy(x => x.D)
                .ThenBy(x => x.N.Id)
                .Take(MaxNeighbours)
                .Select(x => x.N)
                .ToList();
            if (neighbours.Count < Math.Max(1, options.MinNeighbours))
            {
                continue;
            }

            var (ma, mb, mh) = (Median(neighbours.Select(n => n.Da)), Median(neighbours.Select(n => n.Db)), Median(neighbours.Select(n => n.Dh)));
            var spread = Median(neighbours.Select(n => Norm(n.Da - ma, n.Db - mb, n.Dh - mh)));
            results[hold.Id] = new Result(Norm(hold.Da - ma, hold.Db - mb, hold.Dh - mh), spread, neighbours.Count);
        }

        return results;
    }

    /// <summary>
    /// The 3D measurements of <paramref name="pairs"/> relative to their neighbourhoods; pairs with no comparable placements or
    /// too few neighbours have none (the caller falls back to the photo estimate).
    /// </summary>
    /// <param name="pairs">Old holds with their successors (placed in 3D, provisionally or for real).</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The measure per old hold id.</returns>
    public static Dictionary<Guid, HoldMoveMeasure> Measure(IReadOnlyList<(Hold Old, Hold Twin)> pairs, HoldMoveOptions options)
    {
        var inputs = new List<Input>();
        foreach (var (old, twin) in pairs)
        {
            if (HoldMoveCalculator.Vector3D(old, twin) is { } v)
            {
                inputs.Add(new Input(old.Id, old.FacetId!, old.PlaneAMm!.Value, old.PlaneBMm!.Value, v.Da, v.Db, v.Dh));
            }
        }

        var field = Compute(inputs, options);
        var twins = pairs.ToDictionary(p => p.Old.Id, p => p.Twin);
        var olds = pairs.ToDictionary(p => p.Old.Id, p => p.Old);
        return field.ToDictionary(
            kv => kv.Key,
            kv => new HoldMoveMeasure(
                kv.Value.ResidualMm,
                HoldMoveSource.ThreeD,
                HoldMoveCalculator.Rotation(HoldFingerprint.FromJson(olds[kv.Key].FingerprintJson), HoldFingerprint.FromJson(twins[kv.Key].FingerprintJson)),
                kv.Value.SpreadMm,
                kv.Value.SpreadMm <= options.MaxSpreadMm,
                HoldMoveCalculator.Distance3D(olds[kv.Key], twins[kv.Key]),
                kv.Value.ResidualMm));
    }

    private static double Norm(double a, double b, double c) => Math.Sqrt((a * a) + (b * b) + (c * c));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
