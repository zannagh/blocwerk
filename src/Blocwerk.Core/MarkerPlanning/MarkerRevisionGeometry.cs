// <copyright file="MarkerRevisionGeometry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// How well one detected marker's apparent geometry fits one revision's plan, measured against its nearest
/// detected neighbours so the camera's distance and (locally) its perspective cancel out: the ratio of
/// apparent sides against the ratio of printed sizes, and — on the same segment — the apparent centre
/// distance in neighbour sides against the planned distance in neighbour sizes. Residuals are absolute log
/// ratios (0 = a perfect fit; ln 2 ≈ 0.69 for a sheet twice or half the planned size).
/// </summary>
internal static class MarkerRevisionGeometry
{
    /// <summary>How many nearest neighbours vote.</summary>
    public const int Neighbours = 4;

    /// <summary>One marker's residual is capped here, below <see cref="MarkerRevisionInference.IdCost"/>.</summary>
    public const double MaxResidual = 1.5;

    /// <summary>The median neighbour residual of <paramref name="marker"/> under <paramref name="plan"/>; 0 without neighbours.</summary>
    public static double Residual(
        ObservedMarker marker, PlanMarker planned, IReadOnlyList<ObservedMarker> neighbours, IReadOnlyDictionary<int, PlanMarker> plan)
    {
        var votes = neighbours
            .Where(n => n.SidePx > 0 && plan[n.Id].SizeMm > 0)
            .OrderBy(n => Distance(marker, n))
            .Take(Neighbours)
            .Select(n => PairResidual(marker, planned, n, plan[n.Id]))
            .Order()
            .ToList();
        if (votes.Count == 0 || marker.SidePx <= 0 || planned.SizeMm <= 0)
        {
            return 0;
        }

        var median = votes.Count % 2 == 1 ? votes[votes.Count / 2] : (votes[(votes.Count / 2) - 1] + votes[votes.Count / 2]) / 2;
        return Math.Min(median, MaxResidual);
    }

    private static double PairResidual(ObservedMarker marker, PlanMarker planned, ObservedMarker neighbour, PlanMarker neighbourPlan)
    {
        var size = Math.Abs(Math.Log((marker.SidePx / neighbour.SidePx) / (planned.SizeMm / neighbourPlan.SizeMm)));
        if (planned.Segment != neighbourPlan.Segment)
        {
            return size;
        }

        var plannedMm = Math.Sqrt(Math.Pow(planned.XMm - neighbourPlan.XMm, 2) + Math.Pow(planned.YMm - neighbourPlan.YMm, 2));
        var seenPx = Distance(marker, neighbour);
        if (plannedMm < neighbourPlan.SizeMm || seenPx <= 0)
        {
            return size;
        }

        var place = Math.Abs(Math.Log((seenPx / neighbour.SidePx) / (plannedMm / neighbourPlan.SizeMm)));
        return size + place;
    }

    private static double Distance(ObservedMarker a, ObservedMarker b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}

/// <summary>One detected marker as the inference reads it.</summary>
/// <param name="Id">The decoded id.</param>
/// <param name="X">Centre x, pixels.</param>
/// <param name="Y">Centre y, pixels.</param>
/// <param name="SidePx">Mean apparent side, pixels.</param>
public sealed record ObservedMarker(int Id, double X, double Y, double SidePx)
{
    /// <summary>A detector result as an observation.</summary>
    public static ObservedMarker From(Abstractions.DetectedMarker marker) =>
        new(marker.Id, marker.CenterPx.X, marker.CenterPx.Y, marker.SidePx);
}

/// <summary>A plan revision that may be on the wall.</summary>
/// <param name="Revision">The revision number.</param>
/// <param name="Plan">The revision's plan.</param>
/// <param name="EffectiveFrom">When the owner said its markers were put up (null: planned, not yet on the wall).</param>
public sealed record RevisionCandidate(int Revision, MarkerPlan Plan, DateTimeOffset? EffectiveFrom);

/// <summary>What a photo's markers say about its revision.</summary>
/// <param name="Revision">The best-supported revision (tie-broken by the "on the wall" date, then the newest).</param>
/// <param name="CompatibleFrom">The lowest revision the photo is compatible with.</param>
/// <param name="CompatibleTo">The highest revision the photo is compatible with.</param>
/// <param name="DecidingIds">The detected ids that differ between candidates (the only evidence).</param>
/// <param name="Costs">Each candidate's cost (lower fits better).</param>
public sealed record MarkerRevisionEvidence(
    int Revision, int CompatibleFrom, int CompatibleTo, IReadOnlyList<int> DecidingIds, IReadOnlyDictionary<int, double> Costs)
{
    /// <summary>True when exactly one revision fits.</summary>
    public bool IsDecisive => CompatibleFrom == CompatibleTo;
}
