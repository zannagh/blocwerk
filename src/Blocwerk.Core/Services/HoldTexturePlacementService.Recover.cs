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
/// <item>a photo that still leaves holds unplaced is registered again with the holds LINKED to it
/// (<see cref="HoldLink"/>, the same physical hold on an overlapping panel) as further anchors, at the
/// positions the other photos' registrations just placed them;</item>
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

        var links = plans.Any(p => p.Summary.Failed > 0) || carried.Count > 0
            ? await LinkedHoldsAsync(db, wallId, ct)
            : Array.Empty<(Guid, Guid)>().ToLookup(x => x.Item1, x => x.Item2);
        if (plans.Any(p => p.Summary.Failed > 0))
        {
            for (var i = 0; i < plans.Count; i++)
            {
                plans[i] = await RetryWithLinksAsync(db, panels[i].Id, panels[i].Holds, plans, i, links, model, carried, ct);
            }
        }

        // The evidence a carried placement must agree with: what the photos registered in this run placed.
        var placed = plans.SelectMany(p => p.Placements).ToDictionary(p => p.Hold.Id, p => p.Fit);
        return plans.Select((p, i) => WithCarried(p, panels[i].Holds, carried, new CarryContext(model.Frames, placed, links))).ToList();
    }

    /// <summary>The panel planned again with its linked holds as anchors, when that places more holds; else its plan.</summary>
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
        var accepted = plan.Summary.Facets.Where(f => f.Accepted).Select(f => f.FacetId).ToHashSet(StringComparer.Ordinal);
        var linked = holds
            .SelectMany(h => links[h.Id].Where(placedElsewhere.ContainsKey).Select(o => (Hold: h, Fit: placedElsewhere[o])))
            .Select(x => new PlaneAnchor(x.Hold.X, x.Hold.Y, x.Fit.FacetId, x.Fit.PlaneAMm, x.Fit.PlaneBMm))
            .ToList();
        var useful = linked.Where(a => !accepted.Contains(a.FacetId)).GroupBy(a => a.FacetId).Any(g => g.Count() >= AnchorSeed.MinAnchors);
        if (plan.Summary.Failed == 0 || !useful)
        {
            return plan;
        }

        logger.LogInformation(
            "Panel {Panel}: {Failed} holds unplaced; registering again with {Linked} linked holds placed from other photos",
            plan.Summary.Label, plan.Summary.Failed, linked.Count);
        var retry = await PlanPanelAsync(db, panelId, holds, model.Textures, [.. OwnAnchors(holds, carried), .. linked], ct);
        return retry.Placements.Count > plan.Placements.Count ? retry : plan;
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
