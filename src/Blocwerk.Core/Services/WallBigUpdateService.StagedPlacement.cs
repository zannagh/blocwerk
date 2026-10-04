// <copyright file="WallBigUpdateService.StagedPlacement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.HoldMoves;

namespace Blocwerk.Core.Services;

/// <summary>
/// 3D for the moves plan BEFORE the update is confirmed: the staged panel photos are registered onto the active model's
/// textures (the registration the new-hold triage already uses, seeded by the old holds' placements) and each staged hold
/// is placed with <see cref="HoldTexturePlacer"/>'s own math. Nothing is stored; no model, textures or registration means
/// no placement and the plan falls back to the photo estimate.
/// </summary>
public partial class WallBigUpdateService
{
    private async Task<IReadOnlyDictionary<Guid, StagedPlacement>> ProvisionalPlacementsAsync(
        BlocwerkDbContext db, IReadOnlyList<(Hold Old, Hold Twin)> pairs)
    {
        if (stagedPlacer is not null)
        {
            return await stagedPlacer.PlaceAsync(pairs);
        }

        var result = new Dictionary<Guid, StagedPlacement>();
        if (pairs.Count == 0)
        {
            return result;
        }

        var carryover = pairs.Select(p => new CarryoverProposal(p.Old.Id, p.Twin.Id, 1, 0)).ToList();
        var model = await LoadEvidence3DAsync(db, pairs[0].Twin.WallId, carryover, pairs.Select(p => p.Old).ToList());
        if (model is null)
        {
            return result;
        }

        foreach (var panel in pairs.Where(p => p.Twin.WallPanelId is not null).GroupBy(p => p.Twin.WallPanelId!.Value))
        {
            var evidence = await PanelEvidenceAsync(db, model, panel.Key, panel.ToList(), needed: true);
            if (evidence is null)
            {
                continue;
            }

            foreach (var (_, twin) in panel)
            {
                if (HoldTexturePlacer.Place(twin, evidence.Evidence.Registrations) is { } fit)
                {
                    result[twin.Id] = new StagedPlacement(fit.FacetId, fit.PlaneAMm, fit.PlaneBMm);
                }
            }
        }

        return result;
    }
}
