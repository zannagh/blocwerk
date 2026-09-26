// <copyright file="HoldTexturePlacementService.Consistency.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>A placement a photo's registration made, with the evidence the consistency check weighs.</summary>
/// <param name="Plan">Index of the panel plan it belongs to.</param>
/// <param name="Placement">The placement.</param>
/// <param name="Side">Its world position and evidence.</param>
/// <param name="Unsupported">
/// Whether it lies beyond its registration's inlier hull by more than <see cref="InlierSupport.ExtrapolationLimitMm"/>, or an
/// overlapping facet's registration of the same photo puts it elsewhere and neither is clearly better.
/// </param>
internal sealed record CheckedPlacement(int Plan, PlannedPlacement Placement, TwinSide Side, bool Unsupported);

/// <summary>
/// Consistency of the registered placements before anything is written: the two photos of a linked ("same hold")
/// pair must put it at the same spot (<see cref="TwinConsistency"/>), and a hold placed far beyond its registration's
/// inliers (<see cref="InlierSupport"/>) must be confirmed by its twin. A placement that fails is not written and the
/// hold is not measured: a hold without a position is honest, one drawn on bare wall is not.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>The plans without the placements the consistency check drops; <paramref name="dropped"/> gets their holds.</summary>
    private List<PanelPlan> Consistent(List<PanelPlan> plans, ILookup<Guid, Guid> links, ActiveModel model, out HashSet<Guid> dropped)
    {
        var placed = CheckedPlacements(plans, model);
        var (disagreed, confirmed) = JudgeTwins(placed, links, plans);
        var unsupported = placed.Values
            .Where(p => p.Unsupported && !confirmed.Contains(p.Placement.Hold.Id) && !disagreed.Contains(p.Placement.Hold.Id))
            .Select(p => p.Placement.Hold.Id)
            .ToHashSet();
        foreach (var p in placed.Values.Where(p => unsupported.Contains(p.Placement.Hold.Id)))
        {
            logger.LogInformation(
                "Panel {Panel}: hold {HoldId} on facet {FacetId} lies {Beyond:F0} mm beyond the registration's inliers or its photo's "
                + "registrations disagree on it, and no linked hold confirms it; not measured",
                plans[p.Plan].Summary.Label, p.Placement.Hold.Id, p.Placement.Fit.FacetId, p.Side.BeyondHullMm);
        }

        dropped = [.. disagreed, .. unsupported];
        return plans.Select(p => WithoutDropped(p, placed, disagreed, unsupported)).ToList();
    }

    /// <summary>
    /// Every registered placement with its evidence, by hold. Where two registrations of the same photo both contain
    /// the hold (overlapping facets), the one the evidence favours places it (<see cref="Reconcile"/>).
    /// </summary>
    private Dictionary<Guid, CheckedPlacement> CheckedPlacements(List<PanelPlan> plans, ActiveModel model)
    {
        var result = new Dictionary<Guid, CheckedPlacement>();
        for (var i = 0; i < plans.Count; i++)
        {
            var evidence = (plans[i].Registrations ?? [])
                .Where(r => r.Accepted && model.Frames.ContainsKey(r.FacetId))
                .GroupBy(r => r.FacetId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new PhotoEvidence(g.First(), InlierSupport.Of(g.First())), StringComparer.Ordinal);
            foreach (var p in plans[i].Placements.Where(p => evidence.ContainsKey(p.Fit.FacetId)))
            {
                result[p.Hold.Id] = Reconcile(i, p, evidence, model, plans[i].Summary.Label);
            }

            logger.LogInformation(
                "Panel {Panel}: {Placed} registered placements, {Extrapolated} beyond their registration's inliers by more than {Share:P0} "
                + "of the facet or contested by an overlapping facet's registration",
                plans[i].Summary.Label, plans[i].Placements.Count, result.Values.Count(c => c.Plan == i && c.Unsupported),
                InlierSupport.MaxExtrapolationShare);
        }

        return result;
    }

    /// <summary>The linked pairs on the same facet, judged: the holds whose placement loses, and those a twin confirms.</summary>
    private (HashSet<Guid> Disagreed, HashSet<Guid> Confirmed) JudgeTwins(
        Dictionary<Guid, CheckedPlacement> placed, ILookup<Guid, Guid> links, List<PanelPlan> plans)
    {
        var (disagreed, confirmed) = (new HashSet<Guid>(), new HashSet<Guid>());
        foreach (var (id, first) in placed)
        {
            foreach (var twinId in links[id].Where(t => t.CompareTo(id) > 0 && placed.ContainsKey(t)))
            {
                var second = placed[twinId];
                if (first.Placement.Fit.FacetId != second.Placement.Fit.FacetId)
                {
                    continue;
                }

                var verdict = TwinConsistency.Judge(first.Side, second.Side);
                if (verdict == TwinVerdict.Agree)
                {
                    confirmed.UnionWith([id, twinId]);
                    continue;
                }

                LogDisagreement(first, second, verdict, plans);
                if (verdict is TwinVerdict.FirstWrong or TwinVerdict.BothWrong)
                {
                    disagreed.Add(id);
                }

                if (verdict is TwinVerdict.SecondWrong or TwinVerdict.BothWrong)
                {
                    disagreed.Add(twinId);
                }
            }
        }

        confirmed.ExceptWith(disagreed);
        return (disagreed, confirmed);
    }

    private void LogDisagreement(CheckedPlacement first, CheckedPlacement second, TwinVerdict verdict, List<PanelPlan> plans) =>
        logger.LogInformation(
            "Facet {FacetId}: linked holds {First} ({FirstPanel}, score {FirstScore:F0}) and {Second} ({SecondPanel}, score {SecondScore:F0}) "
            + "are placed {Distance:F0} mm apart (limit {Limit:F0} mm): {Verdict}",
            first.Placement.Fit.FacetId, first.Placement.Hold.Id, plans[first.Plan].Summary.Label, TwinConsistency.Score(first.Side),
            second.Placement.Hold.Id, plans[second.Plan].Summary.Label, TwinConsistency.Score(second.Side),
            TwinConsistency.Distance(first.Side, second.Side), TwinConsistency.Threshold(first.Side, second.Side), verdict);

    /// <summary>
    /// The plan without its dropped placements: they count as failed, and each hold loses any placement it had and is
    /// marked not measured (<see cref="PanelPlan.Cleared"/>, revertable).
    /// </summary>
    private static PanelPlan WithoutDropped(
        PanelPlan plan, Dictionary<Guid, CheckedPlacement> placed, HashSet<Guid> disagreed, HashSet<Guid> unsupported)
    {
        var d = plan.Placements.Count(p => disagreed.Contains(p.Hold.Id));
        var u = plan.Placements.Count(p => unsupported.Contains(p.Hold.Id));
        bool Dropped(Hold h) => disagreed.Contains(h.Id) || unsupported.Contains(h.Id);
        var s = plan.Summary;
        return plan with
        {
            Summary = s with { Placed = s.Placed - d - u, Failed = s.Failed + d + u, Disagreed = d, Unsupported = u },
            Placements = plan.Placements
                .Where(p => !Dropped(p.Hold))
                .Select(p => placed.TryGetValue(p.Hold.Id, out var c) ? c.Placement : p)
                .ToList(),
            Cleared = [.. plan.Cleared ?? [], .. plan.Placements.Select(p => p.Hold).Where(Dropped)],
        };
    }
}
