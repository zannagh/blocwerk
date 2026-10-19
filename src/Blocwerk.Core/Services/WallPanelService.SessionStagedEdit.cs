// <copyright file="WallPanelService.SessionStagedEdit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The staged-hold edits as the update's UI and API make them: scoped to the update session the caller is looking at,
/// counted as activity on that session (so the idle sweep never discards an update somebody is editing), and (for a
/// delete) guarded against taking an old hold's carry verdict with its twin. Add and move run the plain staged edit
/// (WallPanelService.StagedEdit.cs) after the check; the delete releases the twin and removes the hold in ONE save, so
/// a failure can never leave a verdict re-pointed for a hold that is still there. The session guard runs first; every
/// write then happens inside the wall's open wall-update journal batch, so that batch alone replays the whole update.
/// </summary>
public partial class WallPanelService
{
    /// <inheritdoc/>
    public async Task<Guid> AddStagedHoldInSessionAsync(
        Guid wallId, Guid? expectedSessionId, Guid panelId, double x, double y, double radius, bool needsReview = true)
    {
        var userId = await EnsureSessionForStagedEditAsync(wallId, expectedSessionId);
        using (BeginUpdateBatch(wallId))
        {
            var id = await AddStagedHoldAsync(wallId, panelId, x, y, radius, needsReview: needsReview);
            await TouchOpenSessionAsync(wallId, userId);
            return id;
        }
    }

    /// <inheritdoc/>
    public async Task UpdateStagedHoldInSessionAsync(Guid wallId, Guid? expectedSessionId, Guid holdId, double x, double y, double radius)
    {
        var userId = await EnsureSessionForStagedEditAsync(wallId, expectedSessionId);
        using (BeginUpdateBatch(wallId))
        {
            await UpdateStagedHoldAsync(wallId, holdId, x, y, radius);
            await TouchOpenSessionAsync(wallId, userId);
        }
    }

    /// <inheritdoc/>
    public async Task<int> DeleteStagedHoldInSessionAsync(
        Guid wallId, Guid? expectedSessionId, Guid holdId, StagedTwinDeletion twins = StagedTwinDeletion.Refuse)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);
        var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
        WallUpdateSessions.EnsureIs(wallId, expectedSessionId, open, WallUpdateSessionSupersededException.ChangeRefused);

        // Same scoping as the plain delete: only a staged-generation hold of the in-flight update. Staged rows carry no
        // boulders, so no boulder rescue is needed.
        var hold = await LoadStagedHoldAsync(db, wallId, holdId);
        var released = open is null ? 0 : await ReleaseTwinVerdictsAsync(db, open, holdId, twins, user.Id);
        await HoldDeletion.UnstackAsync(db, [hold.Id]);
        db.Holds.Remove(hold);
        if (open is not null)
        {
            WallUpdateSessions.Touch(open, user.Id);
        }

        // Guards and the twin refusal ran above, so a refused delete opens no batch; the one save (hold delete, unstack,
        // released verdicts, session touch) lands in the wall's open wall-update batch, like the plain staged delete.
        // Only the allow-listed rows (the hold and its stack links) are journalled; session rows are not, by design.
        using (BeginUpdateBatch(wallId))
        {
            await db.SaveChangesAsync();
        }

        logger.LogInformation(
            "Staged hold {HoldId} deleted on wall {WallId} by {UserId} ({Released} carry verdict(s) released from it)",
            holdId, wallId, user.Id, released);
        return released;
    }

    /// <summary>
    /// The carry verdicts naming <paramref name="holdId"/> as their twin: refused, or re-pointed to "no twin" (same kind,
    /// unconfirmed) in the caller's unit of work, so the cascade of the delete finds nothing of the user's to take.
    /// </summary>
    private static async Task<int> ReleaseTwinVerdictsAsync(
        BlocwerkDbContext db, WallUpdateSession open, Guid holdId, StagedTwinDeletion twins, Guid userId)
    {
        var rows = await db.WallUpdateHoldDecisions
            .Where(d => d.SessionId == open.Id && d.Kind == WallUpdateHoldDecisionKind.Carry && d.PairedHoldId == holdId)
            .ToListAsync();
        if (rows.Count > 0 && twins == StagedTwinDeletion.Refuse)
        {
            throw new StagedHoldIsTwinException(holdId, rows.Select(r => r.HoldId).ToList());
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            CarryConfirmationPolicy.Apply(row, row.CarryKind, null, confirmed: false, userId, now);
            row.UpdatedAt = now;
        }

        return rows.Count;
    }

    /// <summary>Refuses a staged edit from a tab whose update session is no longer the wall's open one; returns the editor.</summary>
    private async Task<Guid> EnsureSessionForStagedEditAsync(Guid wallId, Guid? expectedSessionId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);
        await WallUpdateSessions.EnsureCurrentAsync(db, wallId, expectedSessionId, message: WallUpdateSessionSupersededException.ChangeRefused);
        return user.Id;
    }

    /// <summary>Records an edit as activity on the wall's open update session (who, when), if one is open.</summary>
    private async Task TouchOpenSessionAsync(Guid wallId, Guid userId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        await db.WallUpdateSessions
            .Where(s => s.WallId == wallId && s.Status == WallUpdateSessionStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.LastActiveByUserId, (Guid?)userId));
    }
}
