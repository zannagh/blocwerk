// <copyright file="PanelRedetectService.Apply.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Applying a plan. Shapes, new holds and removals are written first, as one save in one journal batch under the
/// wall's hold lock. The merges come last, each through the duplicate review's own merge (its own lock and batch):
/// nothing touches a merged hold afterwards, so every merge stays undoable there.
/// </summary>
public sealed partial class PanelRedetectService
{
    private const string Busy = "Another update of this wall's holds (a clean-up, an outline upgrade, a merge or a wall update) is running. Try again in a moment.";

    /// <inheritdoc/>
    public async Task<PanelRedetectSummary> ApplyAsync(PanelRedetectPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PanelRedetectSummary summary;
        var (db, userId) = await OpenForEditorAsync(plan.WallId, ct);
        await using (db)
        {
            using var wallLock = WallHoldWriteLock.TryAcquire(plan.WallId, Busy);
            var (wall, panel) = await LoadPanelAsync(db, plan.WallId, plan.PanelId, ct);
            if (panel is null)
            {
                return PanelRedetectSummary.Nothing;
            }

            var input = await LoadInputAsync(db, panel, plan.Detections, userId, ct);
            var decisions = RedetectPlanner.Plan(input, options);
            summary = Summarize(input, decisions);
            if (decisions.Shapes.Count + decisions.NewDetections.Count + decisions.Removed.Count > 0)
            {
                await WriteAsync(db, wall, panel, input, decisions, ct);
            }
        }

        var merged = await MergeAsync(plan.WallId, summary.Merges, ct);
        summary = summary with { Merges = merged, FailedMerges = summary.Merges.Count - merged.Count };
        logger.LogInformation(
            "Panel {PanelId} re-detected on wall {WallId} by {UserId}: {Detected} detections, {Shapes} shapes replaced, {Filled} outlines filled, "
            + "{New} new holds, {Removed} removed, {Merged} merged ({Failed} failed), {Review} left for review, {Skipped} skipped",
            plan.PanelId, plan.WallId, userId, summary.Detected, summary.BetterShapes, summary.FilledOutlines,
            summary.NewHolds, summary.Removed, merged.Count, summary.FailedMerges, summary.ForReview, summary.Skipped);
        return summary;
    }

    /// <summary>A detection as a new stored hold of the panel, with the colour read off the photo as its proposal.</summary>
    private static Hold NewHold(RedetectDetection detection, WallPanel panel)
    {
        var hold = detection.Shape.Clone();
        hold.Id = Guid.NewGuid();
        hold.Generation = panel.Generation;
        hold.Color = detection.Color;
        hold.ProposedColor = detection.Color;
        return hold;
    }

    private async Task WriteAsync(
        BlocwerkDbContext db, Wall wall, WallPanel panel, RedetectInput input, RedetectDecisions decisions, CancellationToken ct)
    {
        using var batch = journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wall.Id);
        var byId = input.Existing.ToDictionary(e => e.Hold.Id, e => e.Hold);
        foreach (var update in decisions.Shapes)
        {
            RedetectShapes.Apply(byId[update.HoldId], input.Detections[update.Detection].Shape, update.Change);
        }

        var added = decisions.NewDetections.Select(i => NewHold(input.Detections[i], panel)).ToList();
        db.Holds.AddRange(added);
        if (decisions.Removed.Count > 0)
        {
            // No boulder uses these (the planner's rule), so only lineage rows can still point at them.
            await HoldDeletion.PrepareHoldsForDeleteAsync(db, decisions.Removed, HoldDeleteBoulderPolicy.LeaveUntouched, ct: ct);
            db.Holds.RemoveRange(decisions.Removed.Select(id => byId[id]));
        }

        if (decisions.NewDetections.Count > 0)
        {
            // New holds are not linked to their twins on the neighbour photos yet: the editor has to look at linking again.
            wall.LinksFinalizedGeneration = null;
        }

        // A new hold inside a clearly larger one sits on it, as after any other detection.
        await HoldStacker.StackNewAsync(db, added, ct);
        ct.ThrowIfCancellationRequested();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Merges the sure pairs one by one; a pair that cannot be merged any more is left as it is.</summary>
    private async Task<List<RedetectMergeInfo>> MergeAsync(Guid wallId, IReadOnlyList<RedetectMergeInfo> merges, CancellationToken ct)
    {
        var done = new List<RedetectMergeInfo>();
        if (duplicates is null)
        {
            return done;
        }

        foreach (var merge in merges)
        {
            try
            {
                var result = await duplicates.MergeAsync(wallId, merge.KeptId, merge.RemovedId, HoldMergeMode.KeepLeft, ct);
                done.Add(merge with { MovedBoulders = result.MovedBoulders, BatchId = result.BatchId });
            }
            catch (Exception ex) when (ex is UserFacingException or KioskRestrictedException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Re-detect on wall {WallId}: hold {Removed} could not be merged into {Kept}", wallId, merge.RemovedId, merge.KeptId);
            }
        }

        return done;
    }
}
