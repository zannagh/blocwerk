// <copyright file="MarkerRevisionInference.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Which plan revision a photo actually SHOWS, from its markers rather than from dates: an owner saves
/// revision N+1, prints, and swaps the sheets days later, so photos in between still show revision N.
/// <para>
/// Only DECIDING markers carry evidence: detected ids whose plan entry is not the same in every candidate
/// (<see cref="MarkerPlanDiff"/>). A deciding id missing from a revision costs <see cref="IdCost"/> (a new id
/// means the newer revision, an old-only id the older one). A deciding id present in a revision costs how badly
/// its apparent size and distance, relative to its nearest detected neighbours, disagree with what that revision
/// plans (<see cref="MarkerRevisionGeometry"/>) — so a filler shrunk under its id is told apart by its size next
/// to unchanged neighbours. Every revision within <see cref="Margin"/> of the cheapest is compatible.
/// </para>
/// <para>
/// With no deciding marker (only markers unchanged across all candidates are visible) every revision is
/// compatible, and the choice does not matter for mapping: only those unchanged markers are there to map.
/// Inside the compatible set the revision "on the wall" at the photo's time (<see cref="RevisionCandidate.EffectiveFrom"/>)
/// wins; without any such date, the newest compatible one (what photos were tagged with before).
/// </para>
/// </summary>
public static class MarkerRevisionInference
{
    /// <summary>The cost of a detected id that a revision does not plan at all.</summary>
    public const double IdCost = 2.0;

    /// <summary>Revisions whose cost is within this of the cheapest are compatible with the photo.</summary>
    public const double Margin = 0.25;

    /// <summary>Infers the revision <paramref name="observed"/> shows; null without candidates.</summary>
    /// <param name="observed">The photo's detected markers (pixels).</param>
    /// <param name="candidates">The wall's plan revisions that may be on the wall.</param>
    /// <param name="at">When the photo was taken or detected (for the <see cref="RevisionCandidate.EffectiveFrom"/> prior).</param>
    public static MarkerRevisionEvidence? Infer(
        IReadOnlyList<ObservedMarker> observed, IReadOnlyList<RevisionCandidate> candidates, DateTimeOffset at)
    {
        var seen = observed.GroupBy(o => o.Id).Select(g => g.First()).ToList();
        return Evaluate(seen.Select(o => o.Id), candidates, at, (deciding, anchors, plan) =>
            seen.Where(o => deciding.Contains(o.Id)).Sum(o => Cost(o, seen, anchors, plan)));
    }

    /// <summary>
    /// Infers the revision a solved capture shows from each marker's size as the photos measured it
    /// (<c>measuredSideMm</c>, see <see cref="Geometry.MarkerSizeCheck"/>): a deciding marker whose measured size
    /// clearly differs from a revision's print costs the log of the size ratio; null without candidates.
    /// </summary>
    public static MarkerRevisionEvidence? InferFromMeasuredSizes(
        IReadOnlyDictionary<int, double> measuredSideMm, IReadOnlyList<RevisionCandidate> candidates, DateTimeOffset at) =>
        Evaluate(measuredSideMm.Keys, candidates, at, (deciding, _, plan) =>
            deciding.Where(measuredSideMm.ContainsKey).Sum(id => SizeCost(id, measuredSideMm[id], plan)));

    /// <summary>The revision marked as put up most recently at <paramref name="at"/>, or null when none is.</summary>
    public static int? OnTheWall(IEnumerable<RevisionCandidate> candidates, DateTimeOffset at) =>
        candidates
            .Where(c => c.EffectiveFrom is { } from && from <= at)
            .OrderByDescending(c => c.EffectiveFrom)
            .ThenByDescending(c => c.Revision)
            .Select(c => (int?)c.Revision)
            .FirstOrDefault();

    /// <summary>
    /// The detected ids whose plan entry differs between any two candidates (added, removed, moved, resized,
    /// reassigned). An id no candidate plans is no evidence (a false positive) and is left out.
    /// </summary>
    internal static HashSet<int> DecidingIds(IEnumerable<int> ids, IReadOnlyList<Dictionary<int, PlanMarker>> plans)
    {
        var deciding = new HashSet<int>();
        foreach (var id in ids)
        {
            var entries = plans.Select(p => p.GetValueOrDefault(id)).ToList();
            if (entries.All(e => e is null))
            {
                continue;
            }

            var first = entries[0];
            if (entries.Any(e => e is null || first is null || !Same(first, e)))
            {
                deciding.Add(id);
            }
        }

        return deciding;
    }

    private static MarkerRevisionEvidence? Evaluate(
        IEnumerable<int> seenIds,
        IReadOnlyList<RevisionCandidate> candidates,
        DateTimeOffset at,
        Func<HashSet<int>, HashSet<int>, Dictionary<int, PlanMarker>, double> cost)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var ids = seenIds.Distinct().ToList();
        var plans = candidates.ToDictionary(c => c.Revision, c => ById(c.Plan.Markers));
        var deciding = DecidingIds(ids, plans.Values.ToList());
        var anchors = ids.Where(id => !deciding.Contains(id) && plans.Values.First().ContainsKey(id)).ToHashSet();
        var costs = plans.ToDictionary(kv => kv.Key, kv => cost(deciding, anchors, kv.Value));
        var best = costs.Values.Min();
        var compatible = costs.Where(kv => kv.Value <= best + Margin).Select(kv => kv.Key).Order().ToList();
        var chosen = Choose(compatible, costs, candidates, at);
        return new MarkerRevisionEvidence(chosen, compatible.Min(), compatible.Max(), deciding.Order().ToList(), costs);
    }

    private static bool Same(PlanMarker a, PlanMarker b) => MarkerPlanDiff.Compare([a], [b]).IsEmpty;

    private static double Cost(
        ObservedMarker marker, List<ObservedMarker> seen, HashSet<int> anchors, Dictionary<int, PlanMarker> plan)
    {
        if (!plan.TryGetValue(marker.Id, out var planned))
        {
            return IdCost;
        }

        // Unchanged neighbours are the trustworthy yardstick; without any, every other marker this revision plans.
        var pool = seen.Where(o => o.Id != marker.Id && plan.ContainsKey(o.Id)).ToList();
        var refs = pool.Any(o => anchors.Contains(o.Id)) ? pool.Where(o => anchors.Contains(o.Id)).ToList() : pool;
        return MarkerRevisionGeometry.Residual(marker, planned, refs, plan);
    }

    private static double SizeCost(int id, double measuredMm, Dictionary<int, PlanMarker> plan)
    {
        if (!plan.TryGetValue(id, out var planned))
        {
            return IdCost;
        }

        return Geometry.MarkerSizeCheck.Mismatch(planned.SizeMm, measuredMm) is null || measuredMm <= 0
            ? 0
            : Math.Min(Math.Abs(Math.Log(measuredMm / planned.SizeMm)), MarkerRevisionGeometry.MaxResidual);
    }

    private static int Choose(
        List<int> compatible, Dictionary<int, double> costs, IReadOnlyList<RevisionCandidate> candidates, DateTimeOffset at)
    {
        if (compatible.Count == 1)
        {
            return compatible[0];
        }

        if (OnTheWall(candidates, at) is not { } onWall)
        {
            return compatible.Max();
        }

        if (compatible.Contains(onWall))
        {
            return onWall;
        }

        // The photo contradicts the revision marked as put up: the evidence wins, ties go to the nearest one.
        return compatible.OrderBy(r => costs[r]).ThenBy(r => Math.Abs(r - onWall)).ThenByDescending(r => r).First();
    }

    private static Dictionary<int, PlanMarker> ById(IEnumerable<PlanMarker> markers)
    {
        var map = new Dictionary<int, PlanMarker>();
        foreach (var marker in markers)
        {
            map.TryAdd(marker.Id, marker);
        }

        return map;
    }
}
