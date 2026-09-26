// <copyright file="HoldTexturePlacementService.Recover.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Planning every panel photo, robust to a photo whose coarse match fails on a new model's textures:
/// <list type="number">
/// <item>each photo is registered with its own holds' previous placements (<see cref="CarriedPositionsAsync"/>) as
/// anchors, so a facet the coarse search misses is searched around the view they predict;</item>
/// <item>a photo that still leaves holds unplaced, or registered a facet only weakly
/// (<see cref="RegistrationAttempts.IsStrong"/>: under 300 inliers or 30 % coverage), is registered again with the holds LINKED to it
/// (<see cref="HoldLink"/>, the same physical hold on an overlapping panel) as further anchors, at the
/// positions the other photos' registrations just placed them, and the better plan kept;</item>
/// <item>whatever is still unplaced keeps its previous placement, carried over and marked
/// <see cref="HoldMetric.TextureRegistrationCarried"/> (revertable like the rest of the run).</item>
/// </list>
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>The plans of every panel photo of <paramref name="live"/>, in panel id order.</summary>
    private async Task<List<PanelPlan>> PlanPanelsAsync(
        BlocwerkDbContext db, Guid wallId, List<Hold> live, ActiveModel model, CancellationToken ct)
    {
        var carried = await CarriedPositionsAsync(db, wallId, model, live, ct);
        var panels = live.Where(h => h.WallPanelId is not null)
            .GroupBy(h => h.WallPanelId!.Value)
            .OrderBy(g => g.Key)
            .Select(g => (Id: g.Key, Holds: g.ToList()))
            .ToList();
        var plans = new List<PanelPlan>();
        foreach (var (id, holds) in panels)
        {
            plans.Add(await PlanPanelAsync(db, id, holds, model.Textures, OwnAnchors(holds, carried), ct));
        }

        var links = await LinkedHoldsAsync(db, wallId, ct);
        if (plans.Any(p => p.Summary.Failed > 0 || WeakFacets(p).Count > 0))
        {
            for (var i = 0; i < plans.Count; i++)
            {
                plans[i] = await RetryWithLinksAsync(db, panels[i].Id, panels[i].Holds, plans, i, links, model, carried, ct);
            }
        }

        // Placements the linked holds or the registration's own inliers contradict are not written (the hold is not measured).
        plans = Consistent(plans, links, model, out var dropped);

        // The evidence a carried placement must agree with: what the photos registered in this run placed.
        var placed = plans.SelectMany(p => p.Placements).ToDictionary(p => p.Hold.Id, p => p.Fit);
        var context = new CarryContext(model.Frames, placed, links);
        return plans.Select((p, i) => WithCarried(p, panels[i].Holds, carried, context, dropped)).ToList();
    }

    /// <summary>
    /// The panel planned again with its linked holds as anchors (its registrations so far as the starting point), when it
    /// left holds unplaced or has a weak facet the links reach, and that plan is better; else its plan.
    /// </summary>
    private async Task<PanelPlan> RetryWithLinksAsync(
        BlocwerkDbContext db,
        Guid panelId,
        List<Hold> holds,
        List<PanelPlan> plans,
        int index,
        ILookup<Guid, Guid> links,
        ActiveModel model,
        Dictionary<Guid, CarriedPosition> carried,
        CancellationToken ct)
    {
        var plan = plans[index];
        var placedElsewhere = plans.Where((_, j) => j != index).SelectMany(p => p.Placements).ToDictionary(p => p.Hold.Id, p => p.Fit);
        var weak = WeakFacets(plan);
        var linked = holds
            .SelectMany(h => links[h.Id].Where(placedElsewhere.ContainsKey).Select(o => (Hold: h, Fit: placedElsewhere[o])))
            .Select(x => new PlaneAnchor(x.Hold.X, x.Hold.Y, x.Fit.FacetId, x.Fit.PlaneAMm, x.Fit.PlaneBMm))
            .ToList();
        var useful = linked.Where(a => weak.Contains(a.FacetId)).GroupBy(a => a.FacetId).Any(g => g.Count() >= AnchorSeed.MinAnchors);
        if (!useful)
        {
            return plan;
        }

        logger.LogInformation(
            "Panel {Panel}: {Failed} holds unplaced, weak facets {Weak}; registering again with {Linked} linked holds placed from other photos",
            plan.Summary.Label, plan.Summary.Failed, string.Join(", ", weak.Order(StringComparer.Ordinal)), linked.Count);
        var retry = await PlanPanelAsync(db, panelId, holds, model.Textures, [.. OwnAnchors(holds, carried), .. linked], ct, plan.Registrations);
        return Better(retry, plan) ? retry : plan;
    }

    /// <summary>The facets a plan's photo registered only weakly or not at all (none when it registered nothing: there is nothing to retry).</summary>
    private static HashSet<string> WeakFacets(PanelPlan plan) =>
        plan.Registrations is { } registrations
            ? registrations.Where(r => !RegistrationAttempts.IsStrong(r)).Select(r => r.FacetId).ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>More placements wins; on a tie the stronger registrations (inliers × coverage, summed).</summary>
    private static bool Better(PanelPlan candidate, PanelPlan current)
    {
        if (candidate.Placements.Count != current.Placements.Count)
        {
            return candidate.Placements.Count > current.Placements.Count;
        }

        static double Score(PanelPlan p) => p.Registrations?.Where(r => r.Accepted).Sum(RegistrationAttempts.Score) ?? 0;
        return Score(candidate) > Score(current);
    }

    /// <summary>The same-hold links of the wall, both ways.</summary>
    private static async Task<ILookup<Guid, Guid>> LinkedHoldsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var links = await db.HoldLinks.AsNoTracking()
            .Where(l => l.WallId == wallId && l.Kind == HoldLinkKind.Same)
            .Select(l => new { l.HoldAId, l.HoldBId })
            .ToListAsync(ct);
        return links.SelectMany(l => new[] { (From: l.HoldAId, To: l.HoldBId), (From: l.HoldBId, To: l.HoldAId) })
            .ToLookup(x => x.From, x => x.To);
    }

    /// <summary>The panel's holds at their previous placements, carried onto the active model.</summary>
    private static List<PlaneAnchor> OwnAnchors(List<Hold> holds, Dictionary<Guid, CarriedPosition> carried) =>
        holds.Where(h => carried.ContainsKey(h.Id))
            .Select(h => new PlaneAnchor(h.X, h.Y, carried[h.Id].FacetId, carried[h.Id].A, carried[h.Id].B))
            .ToList();
}
