// <copyright file="WallBigUpdateService.Moves.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which carried holds physically moved, and what that does to their boulders (see <see cref="HoldMovePolicy"/>). The
/// plan is a pure function of the confirmed decisions, the old and staged holds and the matcher's warp, so the confirm
/// screen and the promote derive the same one.
/// </summary>
public partial class WallBigUpdateService
{
    /// <inheritdoc/>
    public async Task<HoldMovePlan> PreviewHoldMovesAsync(Guid wallId, BigUpdateConfirmation confirmation)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
        return await PlanHoldMovesAsync(db, confirmation);
    }

    private async Task<HoldMovePlan> PlanHoldMovesAsync(BlocwerkDbContext db, BigUpdateConfirmation confirmation)
    {
        var skip = confirmation.HandPlacedMergeOldIds?.ToHashSet() ?? [];
        var decisions = confirmation.Carryover
            .Where(d => d.Kind != CarryKind.Removed && d.NewHoldId is not null && !skip.Contains(d.OldHoldId))
            .ToList();
        if (decisions.Count == 0)
        {
            return HoldMovePlan.Empty;
        }

        var ids = decisions.Select(d => d.OldHoldId).Concat(decisions.Select(d => d.NewHoldId!.Value)).Distinct().ToList();
        var holds = await db.Holds.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id);
        var pairs = decisions
            .Where(d => holds.ContainsKey(d.OldHoldId) && holds.ContainsKey(d.NewHoldId!.Value))
            .Select(d => (Old: holds[d.OldHoldId], Twin: holds[d.NewHoldId!.Value]))
            .Where(p => !p.Old.IsVirtual && p.Twin.WallPanelId is not null)
            .ToList();
        var panelIds = pairs.Select(p => p.Twin.WallPanelId!.Value).ToHashSet();
        var sizes = await LoadStagedPhotoSizesAsync(db, panelIds);
        var provisional = await ProvisionalPlacementsAsync(db, pairs);
        pairs = pairs.Select(p => (p.Old, WithPlacement(p.Twin, provisional))).ToList();
        var moves = new List<PlannedMove>();
        foreach (var byPanel in pairs.GroupBy(p => p.Twin.WallPanelId!.Value))
        {
            var size = sizes.TryGetValue(byPanel.Key, out var s) ? s : ((int, int)?)null;
            var scale = size is { } sz ? PanelScaleEstimator.Estimate(byPanel, sz) : null;
            foreach (var (old, twin) in byPanel.OrderBy(p => p.Old.Id))
            {
                var warped = confirmation.CarriedWarpPositions?.GetValueOrDefault(old.Id);
                if (HoldMoveCalculator.Measure(old, twin, warped, size, scale) is { } measure)
                {
                    moves.Add(new PlannedMove(old.Id, twin.Id, measure, HoldMovePolicy.Classify(measure, moveOptions)));
                }
            }
        }

        return new HoldMovePlan(moves);
    }

    /// <summary>
    /// The twin with its provisional 3D position when it has none of its own (an in-memory copy; the row is not touched).
    /// </summary>
    private static Hold WithPlacement(Hold twin, IReadOnlyDictionary<Guid, StagedPlacement> placements)
    {
        if (twin is { FacetId: not null, PlaneAMm: not null } || !placements.TryGetValue(twin.Id, out var p))
        {
            return twin;
        }

        var copy = twin.Clone();
        (copy.Id, copy.FacetId, copy.PlaneAMm, copy.PlaneBMm) = (twin.Id, p.FacetId, p.PlaneAMm, p.PlaneBMm);
        return copy;
    }

    /// <summary>Writes the measurement onto the lineage link of a carried pair.</summary>
    private static void RecordMove(HoldGenerationLink link, PlannedMove? move)
    {
        if (move is null)
        {
            return;
        }

        link.MoveDistanceMm = Math.Round(move.Measure.DistanceMm, 1);
        link.MoveSource = move.Measure.Source;
        link.MoveRotationDeg = move.Measure.RotationDeg is { } r ? Math.Round(r, 1) : null;
        link.MoveOutcome = move.Outcome;
    }
}
