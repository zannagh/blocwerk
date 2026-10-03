// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>One step: revert the carry that came from the target, map the rest, copy the volumes, map the proposals, record it.</summary>
public static partial class CorrectionCarry
{
    private static async Task<CorrectionCarryResult> StepAsync(BlocwerkDbContext db, Guid wallId, CarryStep step, Guid userId, CancellationToken ct)
    {
        var holds = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).ToDictionaryAsync(h => h.Id, ct);
        var restored = await RevertCarriesAsync(db, wallId, step, holds, ct);
        var (volumeIds, copied) = await VolumesAsync(db, step, ct);

        var entries = new List<HoldPlacementEntry>();
        var (placed, unmeasured) = (0, 0);
        foreach (var hold in holds.Values)
        {
            if (!restored.Contains(hold.Id) && HasDerived(hold))
            {
                var entry = HoldPlacementEntry.Before(hold) with
                {
                    CarriedFromModelId = step.From, RestoresVolumePlacement = true, PrevVolumePlacementJson = hold.VolumePlacementJson,
                };
                if (step.DroppedFacet is { } dropped && hold.FacetId == dropped)
                {
                    WallGeometryModelTransformer.ClearHold(hold);
                    unmeasured++;
                }
                else
                {
                    WallGeometryModelTransformer.TransformHold(hold, step.Transform, id => volumeIds.TryGetValue(id, out var to) ? to : null);
                }

                entries.Add(entry with
                {
                    PlacementHash = HoldPlacementEntry.HashPlacement(hold),
                    FingerprintHash = HoldPlacementEntry.HashFingerprint(hold.FingerprintJson),
                });
            }

            placed += hold is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null } ? 1 : 0;
        }

        foreach (var proposal in await db.HoldProposals.Where(p => p.WallId == wallId).ToListAsync(ct))
        {
            WallGeometryModelTransformer.TransformProposal(proposal, step.Transform);
            proposal.GeometryModelId = proposal.GeometryModelId == step.From ? step.To : proposal.GeometryModelId;
        }

        db.HoldPlacementRuns.Add(new HoldPlacementRun
        {
            WallId = wallId,
            GeometryModelId = step.To,
            CreatedByUserId = userId,
            Trigger = HoldPlacementTrigger.Correction,
            HoldsJson = HoldPlacementEntry.ToJson(entries),
            PlacedCount = placed,
            FailedCount = unmeasured,
        });
        await db.SaveChangesAsync(ct);
        return new CorrectionCarryResult(placed, unmeasured, restored.Count, copied);
    }

    /// <summary>
    /// Reverts, exactly, the unreverted carries onto <see cref="CarryStep.From"/> that came from <see cref="CarryStep.To"/>
    /// (going back to a version the data was carried down from): the holds nobody changed since. Returns them.
    /// </summary>
    private static async Task<HashSet<Guid>> RevertCarriesAsync(
        BlocwerkDbContext db, Guid wallId, CarryStep step, Dictionary<Guid, Hold> holds, CancellationToken ct)
    {
        var runs = await db.HoldPlacementRuns
            .Where(r => r.WallId == wallId && r.GeometryModelId == step.From && r.Trigger == HoldPlacementTrigger.Correction && r.RevertedAt == null)
            .ToListAsync(ct);
        var restored = new HashSet<Guid>();
        foreach (var run in runs.OrderByDescending(r => r.CreatedAt))
        {
            var entries = HoldPlacementEntry.FromJson(run.HoldsJson);
            if (entries.Count == 0 || entries[0].CarriedFromModelId != step.To)
            {
                continue;
            }

            var reverted = 0;
            foreach (var entry in entries)
            {
                if (!restored.Contains(entry.HoldId) && holds.TryGetValue(entry.HoldId, out var hold) && entry.TryRevert(hold))
                {
                    restored.Add(hold.Id);
                    reverted++;
                }
            }

            (run.RevertedAt, run.RevertedCount) = (DateTimeOffset.UtcNow, reverted);
        }

        return restored;
    }

    /// <summary>
    /// The target's volumes: when it has none yet, the source's copied (mapped; none on a dropped facet). Returns the
    /// source volume id → target volume id map (by facet and index) and how many were copied.
    /// </summary>
    private static async Task<(Dictionary<Guid, Guid> Ids, int Copied)> VolumesAsync(BlocwerkDbContext db, CarryStep step, CancellationToken ct)
    {
        var source = await db.WallVolumes.AsNoTracking().Where(v => v.GeometryModelId == step.From).ToListAsync(ct);
        var target = await db.WallVolumes.AsNoTracking().Where(v => v.GeometryModelId == step.To).ToListAsync(ct);
        var copied = 0;
        if (target.Count == 0)
        {
            foreach (var volume in source.Where(v => v.FacetId != step.DroppedFacet))
            {
                var copy = WallGeometryModelTransformer.TransformVolume(volume, step.To, step.Transform);
                db.WallVolumes.Add(copy);
                target.Add(copy);
                copied++;
            }
        }

        var byKey = target.GroupBy(v => (v.FacetId, v.Index)).ToDictionary(g => g.Key, g => g.First().Id);
        var ids = source.Where(v => byKey.ContainsKey((v.FacetId, v.Index))).ToDictionary(v => v.Id, v => byKey[(v.FacetId, v.Index)]);
        return (ids, copied);
    }

    /// <summary>Whether the hold carries anything measured on a model.</summary>
    private static bool HasDerived(Hold h) =>
        h.FacetId is not null || h.PlaneAMm is not null || h.PlaneBMm is not null || h.WidthMm is not null
        || h.FootprintMm is not null || h.ProtrusionMm is not null || h.VolumePlacementJson is not null;
}
