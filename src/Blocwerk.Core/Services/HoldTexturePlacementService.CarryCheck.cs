// <copyright file="HoldTexturePlacementService.CarryCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>What the run knows about the holds apart from their carried placements.</summary>
/// <param name="Frames">The active model's facet frames.</param>
/// <param name="Placed">The holds the photos registered in this run placed.</param>
/// <param name="Links">The same-hold links of the wall, both ways.</param>
internal sealed record CarryContext(
    IReadOnlyDictionary<string, FacetFrame> Frames, IReadOnlyDictionary<Guid, HoldPlaneFit> Placed, ILookup<Guid, Guid> Links);

/// <summary>
/// Keeping the previous placements of the holds a photo could not place in this run (<see cref="CarriedPositionsAsync"/>),
/// but only those the run's own evidence does not contradict (<see cref="CarryEvidence"/>): an earlier placement can
/// have been wrong, and carrying it would keep it wrong on every later model.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>
    /// The plan plus the carried-over previous placements of the eligible holds it could not place. A carried
    /// placement the evidence contradicts is dropped, and the hold loses it (<see cref="PanelPlan.Cleared"/>). A hold whose
    /// registered placement the consistency check dropped (<paramref name="dropped"/>) keeps no previous placement either.
    /// </summary>
    private PanelPlan WithCarried(
        PanelPlan plan, List<Hold> holds, Dictionary<Guid, CarriedPosition> carried, CarryContext context, IReadOnlySet<Guid> dropped)
    {
        var placed = plan.Placements.Select(p => p.Hold.Id).ToHashSet();
        var candidates = holds
            .Where(h => HoldTexturePlacer.IsEligible(h) && !placed.Contains(h.Id) && carried.ContainsKey(h.Id) && !dropped.Contains(h.Id))
            .ToList();
        var kept = new List<PlannedPlacement>();
        var cleared = new List<Hold>();
        foreach (var hold in candidates)
        {
            var position = carried[hold.Id];
            var disagreement = Disagreement(hold, position, plan.Registrations ?? [], context);
            if (CarryEvidence.Allows(disagreement))
            {
                kept.Add(CarriedPlacement(hold, position));
                continue;
            }

            logger.LogInformation(
                "Panel {Panel}: hold {HoldId}'s previous placement (facet {FacetId}, {A:F0}/{B:F0} mm) disagrees with this run's evidence "
                + "by {Disagreement:F0} mm; not carried, the hold is not measured",
                plan.Summary.Label, hold.Id, position.FacetId, position.A, position.B, disagreement);
            cleared.Add(hold);
        }

        if (kept.Count == 0 && cleared.Count == 0)
        {
            return plan;
        }

        var s = plan.Summary;
        var summary = s with { Placed = s.Placed + kept.Count, Failed = s.Failed - kept.Count, Carried = kept.Count };
        return plan with { Summary = summary, Placements = [.. plan.Placements, .. kept], Cleared = [.. plan.Cleared ?? [], .. cleared] };
    }

    /// <summary>The largest disagreement of a carried placement with the photo's registration of its facet and its placed linked holds.</summary>
    private static double? Disagreement(Hold hold, CarriedPosition position, IReadOnlyList<FacetRegistration> registrations, CarryContext context)
    {
        var twins = context.Links[hold.Id]
            .Where(context.Placed.ContainsKey)
            .Select(id => context.Placed[id])
            .Select(f => (f.FacetId, f.PlaneAMm, f.PlaneBMm));
        return CarryEvidence.Disagreement(hold.X, hold.Y, (position.FacetId, position.A, position.B), context.Frames, registrations, twins);
    }

    /// <summary>A carried placement: the new position with the hold's size as it was (a re-solve barely changes it).</summary>
    private static PlannedPlacement CarriedPlacement(Hold hold, CarriedPosition position)
    {
        var fit = new HoldPlaneFit(position.FacetId, position.A, position.B, (_, _) => (double.NaN, double.NaN), Wall3DShapeSource.HoldFit);
        var metric = hold is { WidthMm: > 0, HeightMm: > 0, AreaMm2: > 0 }
            ? new HoldMetric(
                hold.WidthMm.Value, hold.HeightMm.Value, hold.AreaMm2.Value, position.FacetId, position.A, position.B,
                HoldMetric.TextureRegistrationCarried)
            : null;
        return new PlannedPlacement(hold, fit, metric, Carried: true);
    }
}
