// <copyright file="WallRefreshProcessor.Apply.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// After the user's confirm: promote what the update SESSION records (so edits made in the full review count),
/// topped up with the matcher's warp dictionaries exactly like the step-by-step wizard — but only when that is what the
/// confirm screen summed up (<see cref="RefreshSummary.DecisionsVersion"/>), otherwise the user checks the new summary
/// first (<see cref="ReconfirmAsync"/>) — then place the holds on
/// the wall's active 3D model when it has textures (<see cref="IHoldTexturePlacementService.PlaceAsync"/>).
/// </summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>The placement run's trigger for runs started by "Update panels + 3D".</summary>
    public const string PlacementTrigger = "update";

    private async Task ApplyAsync(WallRefresh refresh, WallRefreshActors actors, CancellationToken ct)
    {
        await StepAsync(refresh, RefreshTimeline.Apply, RefreshStepState.Running, "Applying the panel update", ct);
        var open = await actors.Sessions.GetOpenSessionAsync(refresh.WallId);
        if (open is null)
        {
            // Promoted already: by this apply before a restart, or from the full review. Either way the new panels
            // are live and the holds still have to be placed; a discarded update leaves nothing to place.
            if (await PromotedAsync(refresh, ct))
            {
                await FinishPromotedAsync(refresh, actors, ct);
                return;
            }

            await FinishAsync(refresh, RefreshStepState.Skipped, "The update was discarded in the full review.", null, ct);
            return;
        }

        if (refresh.UpdateSessionId is { } mine && open.Id != mine)
        {
            throw new UserFacingException("Another update of this wall replaced this one, so it can no longer be applied.");
        }

        var matched = await actors.BigUpdate.ResumeAsync(refresh.WallId);
        var promotable = await RefreshDecisions.LoadAsync(refresh.WallId, actors, matched, await StagedHoldsByPanelAsync(refresh.WallId, ct));
        var summary = RefreshTimeline.Summary(refresh);

        // The version the user CONFIRMED, not the stored summary's: a summary rewritten after the confirm (a re-check, an
        // older app) must not make unseen decisions promotable. Rows accepted before the column existed fall back.
        var confirmed = refresh.ConfirmedDecisionsVersion ?? summary?.DecisionsVersion;
        if (confirmed is null || confirmed != promotable.Version)
        {
            // Not what the user confirmed (edited in the full review, or matched differently now): nothing is
            // promoted; the user sees the summary of what would be, and applies that.
            await ReconfirmAsync(refresh, summary, promotable, open, ct);
            return;
        }

        var confirmation = promotable.Scoped with
        {
            CarriedWarpPositions = matched.CarriedWarpPositions,
            CarriedWarpShapes = matched.CarriedWarpShapes,
            HandPlacedMergeOldIds = (matched.HandPlacedMerges ?? []).Select(m => m.OldHoldId).ToList(),
            ExpectedMovesVersion = promotable.Moves.Version,
        };
        await actors.BigUpdate.PromoteAsync(refresh.WallId, confirmation, open.Id);
        await FinishPromotedAsync(refresh, actors, ct);
    }

    private async Task FinishPromotedAsync(WallRefresh refresh, WallRefreshActors actors, CancellationToken ct)
    {
        await StepAsync(refresh, RefreshTimeline.Review, RefreshStepState.Done, "Confirmed", ct);
        var placed = await PlaceHoldsAsync(refresh, actors.Placement, ct);
        await FinishAsync(refresh, RefreshStepState.Done, "The new panel photos and holds are live", placed, ct);
    }

    private async Task<bool> PromotedAsync(WallRefresh refresh, CancellationToken ct)
    {
        if (refresh.UpdateSessionId is not { } sessionId)
        {
            return false;
        }

        await using var db = dbContextFactory.CreateDbContext();
        return await db.WallUpdateSessions.AnyAsync(s => s.Id == sessionId && s.Status == WallUpdateSessionStatus.Promoted, ct);
    }

    /// <summary>Places the holds on the active 3D model; never fails the update (the panels are already live).</summary>
    private async Task<(RefreshStepState State, string Detail)> PlaceHoldsAsync(
        WallRefresh refresh, IHoldTexturePlacementService? placement, CancellationToken ct)
    {
        if (placement is null)
        {
            return (RefreshStepState.Skipped, "Not available on this server");
        }

        try
        {
            var status = await placement.GetStatusAsync(refresh.WallId, ct);
            if (!status.Enabled || !status.HasTextures)
            {
                var later = refresh.CaptureStarted ? "The 3D capture places them when it finishes." : "This wall has no 3D model yet.";
                return (RefreshStepState.Skipped, later);
            }

            var result = await placement.PlaceAsync(refresh.WallId, PlacementTrigger, ct);
            return (RefreshStepState.Done, $"{result.Placed} holds placed on the 3D model");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (RefreshStepState.Failed, $"Could not place the holds: {ex.Message}");
        }
    }

    private Task FinishAsync(
        WallRefresh refresh, RefreshStepState applyState, string applyDetail, (RefreshStepState State, string Detail)? placed, CancellationToken ct) =>
        SaveAsync(
            refresh,
            r =>
            {
                RefreshTimeline.Set(r, RefreshTimeline.Apply, applyState, applyDetail);
                RefreshTimeline.Set(r, RefreshTimeline.Place, placed?.State ?? RefreshStepState.Skipped, placed?.Detail);
                r.Status = WallRefreshStatus.Done;
                r.Error = null;
                r.CompletedAt = DateTimeOffset.UtcNow;
            },
            ct);
}
