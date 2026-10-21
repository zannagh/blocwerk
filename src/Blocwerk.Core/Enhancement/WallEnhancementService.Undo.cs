// <copyright file="WallEnhancementService.Undo.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The review's writes on what the steps wrote: undoing one hold through the change journal, and confirming suggested
/// colours. Both take the wall's hold lock and land in wall-scoped journal batches of their own.
/// </summary>
public sealed partial class WallEnhancementService
{
    /// <summary>The journal batches the steps of a kind wrote, the newest first (the order an undo takes them back in).</summary>
    /// <param name="run">The run.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>The batch ids.</returns>
    internal IReadOnlyList<Guid> BatchesNewestFirst(WallEnhancementRun run, WallEnhancementItemKind kind)
    {
        var records = WallEnhancementStepRecord.Parse(run.StepsJson);
        return runner.Job.Steps
            .Where(s => s.Kind == kind)
            .Reverse()
            .SelectMany(s => records.FirstOrDefault(r => r.Key == s.Key)?.BatchIds.Reverse() ?? [])
            .ToList();
    }

    private async Task<(WallEnhancementDecision Decision, string Message)> UndoAsync(
        WallEnhancementRun run, WallEnhancementItem item, Guid userId, CancellationToken ct)
    {
        var batches = BatchesNewestFirst(run, item.Kind);
        string? key;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            key = (await WallEnhancementJournal.HoldUpdatesAsync(db, batches, ct, item.HoldId)).FirstOrDefault(e => e.HoldId == item.HoldId)?.KeyJson;
        }

        if (key is null)
        {
            return (WallEnhancementDecision.Skipped, "Nothing was recorded for this hold, so there is nothing to undo.");
        }

        ChangeJournalRevertResult result;
        using (await WallHoldWriteLock.AcquireAsync(run.WallId, ct: ct))
        {
            result = await reverter.RevertKeyAsync(
                new ChangeJournalKeyRevert(
                    batches, nameof(Hold), key, WallEnhancementKinds.UndoLabel(item.Kind), ChangeJournalScopeKind.Wall, run.WallId, userId,
                    WallEnhancementKinds.OwnedColumns(item.Kind)),
                ct);
        }

        if (!result.Reverted)
        {
            return (WallEnhancementDecision.Skipped, "This hold was changed after the enhancement, so it was left as it is.");
        }

        // The old outline needs its 3D footprint back: re-derived in the background, for this hold only.
        if (item.Kind == WallEnhancementItemKind.Shape && item.HoldId is { } holdId)
        {
            refinementQueue?.Enqueue(run.WallId, [holdId]);
        }

        return (WallEnhancementDecision.Rejected, "Undone: the hold is back as it was.");
    }

    private async Task<(WallEnhancementDecision Decision, string Message)> ConfirmColourAsync(
        Guid wallId, WallEnhancementItem item, Guid userId, CancellationToken ct)
    {
        var confirmed = await ConfirmColoursAsync(wallId, [item], userId, ct);
        return (item.Decision, confirmed == 1 ? $"Colour {item.Detail} confirmed." : item.Note ?? "Kept.");
    }

    /// <summary>
    /// Confirms the suggested colours of the items in one journal batch — what setting the colour in the hold editor
    /// does (<see cref="Hold.ProposedColor"/> cleared). A hold whose colour a person set meanwhile counts as kept; a hold
    /// that went is skipped. Records each item's answer; returns how many were confirmed.
    /// </summary>
    private async Task<int> ConfirmColoursAsync(Guid wallId, IReadOnlyList<WallEnhancementItem> items, Guid userId, CancellationToken ct)
    {
        var ids = items.Select(i => i.HoldId).OfType<Guid>().ToList();
        using var writeLock = await WallHoldWriteLock.AcquireAsync(wallId, ct: ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = userId;
        var holds = await db.Holds.Where(h => h.WallId == wallId && ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var confirmed = 0;
        foreach (var item in items)
        {
            if (item.HoldId is not { } id || !holds.TryGetValue(id, out var h))
            {
                Record(item, WallEnhancementDecision.Skipped, "The hold is no longer on the wall.", userId);
                continue;
            }

            if (h.HasProposedColor)
            {
                h.ProposedColor = null;
                confirmed++;
            }

            Record(item, WallEnhancementDecision.Accepted, string.Empty, userId);
        }

        using (FeedScope.Begin(wallId, WallEnhancementKinds.ReviewBatchLabel))
        using (var scope = journal.BeginBatch(WallEnhancementKinds.ReviewBatchLabel, ChangeJournalScopeKind.Wall, wallId))
        {
            await db.SaveChangesAsync(ct);
            if (scope is ChangeJournalBatchScope { BatchRowCreated: true } written)
            {
                foreach (var item in items.Where(i => i.Decision == WallEnhancementDecision.Accepted))
                {
                    item.ResultBatchId = written.BatchId;
                }
            }
        }

        return confirmed;
    }
}
