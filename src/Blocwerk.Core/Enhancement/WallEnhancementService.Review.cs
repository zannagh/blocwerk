// <copyright file="WallEnhancementService.Review.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>Answering the review items: keep or undo what a step wrote, carry out or turn down what a step suggests.</summary>
public sealed partial class WallEnhancementService
{
    /// <inheritdoc/>
    public async Task<WallEnhancementDecisionResult> DecideAsync(Guid wallId, Guid itemId, bool accept, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await ReviewRunAsync(db, wallId, ct);
            var item = await db.WallEnhancementItems.FirstOrDefaultAsync(i => i.Id == itemId && i.RunId == run.Id, ct)
                       ?? throw new UserFacingException("That change is not part of this enhancement.");
            if (item.Decision != WallEnhancementDecision.Pending)
            {
                return new WallEnhancementDecisionResult(item.Decision, "This change was already answered.");
            }

            var (decision, message) = await CarryOutAsync(run, item, accept, userId, ct);
            Record(item, decision, message, userId);
            await db.SaveChangesAsync(ct);
            return new WallEnhancementDecisionResult(decision, message);
        }
    }

    /// <inheritdoc/>
    public async Task<(int Accepted, int Skipped)> AcceptAllAsync(Guid wallId, WallEnhancementItemKind kind, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var run = await ReviewRunAsync(db, wallId, ct);
            var items = await db.WallEnhancementItems
                .Where(i => i.RunId == run.Id && i.Kind == kind && i.Decision == WallEnhancementDecision.Pending)
                .ToListAsync(ct);
            if (kind == WallEnhancementItemKind.Colour)
            {
                await ConfirmColoursAsync(run.WallId, items, userId, ct);
            }
            else
            {
                foreach (var item in items)
                {
                    var (decision, message) = await CarryOutAsync(run, item, true, userId, ct);
                    Record(item, decision, message, userId);
                }
            }

            await db.SaveChangesAsync(ct);
            return (items.Count(i => i.Decision == WallEnhancementDecision.Accepted), items.Count(i => i.Decision == WallEnhancementDecision.Skipped));
        }
    }

    private static void Record(WallEnhancementItem item, WallEnhancementDecision decision, string message, Guid userId)
    {
        item.Decision = decision;
        item.Note = decision == WallEnhancementDecision.Skipped ? (message.Length > 300 ? message[..300] : message) : null;
        item.DecidedAt = DateTimeOffset.UtcNow;
        item.DecidedByUserId = userId;
    }

    /// <summary>The run of the wall's current generation, which must be waiting for its review.</summary>
    private static async Task<WallEnhancementRun> ReviewRunAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var generation = await GenerationAsync(db, wallId, ct);
        var run = await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.WallId == wallId && r.Generation == generation, ct);
        return run is { Status: WallEnhancementStatus.Review }
            ? run
            : throw new UserFacingException(run?.Status == WallEnhancementStatus.Finalized
                ? "This enhancement is already finalized."
                : "No enhancement of this wall is waiting for its review.");
    }

    private async Task<(WallEnhancementDecision Decision, string Message)> CarryOutAsync(
        WallEnhancementRun run, WallEnhancementItem item, bool accept, Guid userId, CancellationToken ct)
    {
        try
        {
            return item.Kind switch
            {
                WallEnhancementItemKind.Shape or WallEnhancementItemKind.Stack => accept
                    ? (WallEnhancementDecision.Accepted, "Kept.")
                    : await UndoAsync(run, item, userId, ct),
                WallEnhancementItemKind.Colour => accept
                    ? await ConfirmColourAsync(run.WallId, item, userId, ct)
                    : await UndoAsync(run, item, userId, ct),
                _ => await SuggestionAsync(run.WallId, item, accept, ct),
            };
        }
        catch (Exception ex) when (ex is UserFacingException or InvalidOperationException)
        {
            return (WallEnhancementDecision.Skipped, ex.Message);
        }
    }

    private async Task<(WallEnhancementDecision Decision, string Message)> SuggestionAsync(
        Guid wallId, WallEnhancementItem item, bool accept, CancellationToken ct)
    {
        var (left, right) = (item.HoldId.GetValueOrDefault(), item.OtherHoldId.GetValueOrDefault());
        switch (item.Kind)
        {
            case WallEnhancementItemKind.Duplicate when accept:
                var mode = Enum.TryParse<HoldMergeMode>(item.Detail, out var parsed) ? parsed : HoldMergeMode.KeepLeft;
                var merged = await duplicates.MergeAsync(wallId, left, right, mode, ct);
                return (WallEnhancementDecision.Accepted, $"Merged; {WallEnhancementKinds.Count(merged.MovedBoulders, "boulder", "boulders")} moved over.");
            case WallEnhancementItemKind.Duplicate:
                await duplicates.DismissAsync(wallId, left, right, ct);
                return (WallEnhancementDecision.Rejected, "Marked as not a duplicate.");
            case WallEnhancementItemKind.Link when accept:
                await links.LinkAsync(wallId, left, right, ct);
                return (WallEnhancementDecision.Accepted, "Linked.");
            case WallEnhancementItemKind.Link:
                await links.RejectAsync(wallId, left, right, ct);
                return (WallEnhancementDecision.Rejected, "Turned down.");
            case WallEnhancementItemKind.NewHold when accept:
                await proposals.AcceptAsync(wallId, item.ReferenceId.GetValueOrDefault(), null, HoldCategory.Hand, ct);
                return (WallEnhancementDecision.Accepted, "Added to the wall.");
            default:
                await proposals.RejectAsync(wallId, item.ReferenceId.GetValueOrDefault(), ct);
                return (WallEnhancementDecision.Rejected, "Turned down.");
        }
    }
}
