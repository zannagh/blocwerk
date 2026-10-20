// <copyright file="WallLookOverService.Missing.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The two answers only a hold the update could not find takes: "it is this hold" and "it is here". Like every other
/// answer they are existing hold operations: the first is the duplicate review's merge of the kept copy into the
/// hold the user points at (the copy's boulders move over, its name and other attributes fill what that hold lacks,
/// the hold's own position and outline stay; wall admins only, undone in the duplicate review), the second is the
/// hold editor's move (which also confirms the hold). Both only for a hold that is live and still not confirmed.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <inheritdoc/>
    public async Task ReassignAsync(
        Guid wallId, Guid findingId, Guid targetHoldId, IReadOnlyCollection<Guid> expectedBoulderIds, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var (finding, view) = await MissingAsync(db, wallId, findingId, access, ct);
            if (!access.CanMerge)
            {
                throw new UserFacingException("Only a wall admin can say that two holds are the same one.");
            }

            if (!expectedBoulderIds.ToHashSet().SetEquals(view.Boulders.Select(b => b.Id)))
            {
                throw new UserFacingException(BouldersChanged);
            }

            // The target is a hold of the same photo that the update did find (or a person placed): never another kept copy.
            var targets = await PanelHoldsAsync(db, wallId, view.PanelId, ct);
            if (!targets.Any(t => t.Id == targetHoldId && !t.Kept))
            {
                throw new UserFacingException("That hold is not on this photo any more. Reload the page.");
            }

            using var batch = BeginBatch(wallId);
            await ClaimForChangeAsync(db, finding, ct);
            try
            {
                var merge = await duplicates.MergeAsync(wallId, targetHoldId, view.HoldId!.Value, HoldMergeMode.KeepLeft, ct);
                (finding.MergeBatchId, finding.OtherHoldId) = (merge.BatchId, targetHoldId);
            }
            catch
            {
                await ReleaseAsync(db, finding);
                throw;
            }

            MarkSettled(finding, LookOverAnswer.Reassign, access.UserId, view.Boulders.Count);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Look-over finding {FindingId} on wall {WallId}: hold reassigned to {HoldId} by {UserId}", findingId, wallId, targetHoldId, access.UserId);
        }
    }

    /// <inheritdoc/>
    public async Task MoveAsync(
        Guid wallId, Guid findingId, double x, double y, double? radius = null, Guid? panelId = null, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var (finding, view) = await MissingAsync(db, wallId, findingId, access, ct);
            if (!access.CanEdit || !view.Answers.Contains(LookOverAnswer.RemoveHold))
            {
                throw new UserFacingException("Only the owner, admins and moderators of this wall can move a hold.");
            }

            var hold = await db.Holds.AsNoTracking().FirstAsync(h => h.Id == view.HoldId, ct);
            await EnsureBareSpotAsync(db, wallId, hold, x, y, panelId, ct);
            using var batch = BeginBatch(wallId);
            await ClaimForChangeAsync(db, finding, ct);
            try
            {
                // The editor's move, as a big-wall panel edit makes it: the boulders are not retired by it.
                await walls.UpdateHoldAsync(
                    hold.Id, new HoldEdit { X = Math.Clamp(x, 0, 1), Y = Math.Clamp(y, 0, 1), Radius = radius ?? hold.Radius, FlagBouldersOnMove = false });
            }
            catch
            {
                await ReleaseAsync(db, finding);
                throw;
            }

            // Placed by a tap: the outline is the old one at a new spot, so the hold asks for a look in the editor.
            await db.Holds.Where(h => h.Id == hold.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.NeedsReview, true), ct);
            MarkSettled(finding, LookOverAnswer.Moved, access.UserId, view.Boulders.Count);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Look-over finding {FindingId} on wall {WallId}: hold moved by {UserId}", findingId, wallId, access.UserId);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PanelHold>> GetPanelHoldsAsync(Guid wallId, Guid panelId, CancellationToken ct = default)
    {
        var (db, _) = await OpenAsync(wallId, ct);
        await using (db)
        {
            return await PanelHoldsAsync(db, wallId, panelId, ct);
        }
    }

    /// <summary>
    /// "It is here" only on the hold's own photo and only on bare wall: a spot on a hold the update found means that hold
    /// ("it is this hold"), as a tap on the page does.
    /// </summary>
    private static async Task EnsureBareSpotAsync(
        BlocwerkDbContext db, Guid wallId, Entities.Hold hold, double x, double y, Guid? panelId, CancellationToken ct)
    {
        if (panelId is { } on && on != hold.WallPanelId)
        {
            throw new UserFacingException("That spot is on another photo: a hold is moved on its own panel's photo.");
        }

        var holds = await PanelHoldsAsync(db, wallId, hold.WallPanelId, ct);
        if (LookOverRules.HoldAtSpot(holds.Where(h => h.Id != hold.Id), x, y) is { } there)
        {
            throw new UserFacingException($"A hold is at that spot ({there.Id}): say that it is this hold instead (Reassign with targetHoldId).");
        }
    }

    private static async Task<List<PanelHold>> PanelHoldsAsync(BlocwerkDbContext db, Guid wallId, Guid? panelId, CancellationToken ct)
    {
        var live = await LivePhotosAsync(db, wallId, ct);
        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.WallId == wallId && h.WallPanelId == panelId && !h.IsVirtual)
            .ToListAsync(ct);
        return holds
            .Where(h => live.Contains((h.WallPanelId, h.Generation)))
            .Select(h => new PanelHold(
                h.Id, h.X, h.Y, h.Radius, h.Color, h.Category, h.ShapePoints, h.IsVirtual, h.ShapeHoles, h.KeptUnconfirmedGeneration is not null, h.StackedOnHoldId))
            .ToList();
    }

    /// <summary>The open finding and its view, when it is about a live hold the update could not find and nobody confirmed.</summary>
    private static async Task<(WallLookOverFinding Finding, LookOverFindingView View)> MissingAsync(
        BlocwerkDbContext db, Guid wallId, Guid findingId, LookOverAccess access, CancellationToken ct)
    {
        var finding = await db.WallLookOverFindings.FirstOrDefaultAsync(f => f.Id == findingId && f.WallId == wallId, ct)
            ?? throw new UserFacingException("That finding no longer exists. Reload the page.");
        var view = (await ToViewsAsync(db, wallId, [finding], access, ct))[0];
        return view.Missing ? (finding, view) : throw new UserFacingException("That hold was already answered or confirmed. Reload the page.");
    }

    private static async Task ClaimForChangeAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        var claimed = await db.WallLookOverFindings
            .Where(f => f.Id == finding.Id && f.Status == LookOverFindingStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, LookOverFindingStatus.Settled), ct);
        if (claimed != 1)
        {
            throw new UserFacingException(AnsweredMeanwhile);
        }
    }

    private static Task ReleaseAsync(BlocwerkDbContext db, WallLookOverFinding finding) =>
        db.WallLookOverFindings.Where(f => f.Id == finding.Id).ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, LookOverFindingStatus.Open));
}
