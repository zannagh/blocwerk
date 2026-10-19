// <copyright file="WallPanelService.SessionStagedEdit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The staged-hold edits as the update's UI and API make them: scoped to the update session the caller is looking at,
/// and (for a delete) guarded against taking an old hold's carry verdict away with its twin. Each checks first and then
/// runs the plain staged edit (WallPanelService.StagedEdit.cs), so that edit's own scoping and journalling apply
/// unchanged.
/// </summary>
public partial class WallPanelService
{
    /// <inheritdoc/>
    public async Task<Guid> AddStagedHoldInSessionAsync(
        Guid wallId, Guid? expectedSessionId, Guid panelId, double x, double y, double radius, bool needsReview = true)
    {
        await EnsureSessionForStagedEditAsync(wallId, expectedSessionId);
        return await AddStagedHoldAsync(wallId, panelId, x, y, radius, needsReview: needsReview);
    }

    /// <inheritdoc/>
    public async Task UpdateStagedHoldInSessionAsync(Guid wallId, Guid? expectedSessionId, Guid holdId, double x, double y, double radius)
    {
        await EnsureSessionForStagedEditAsync(wallId, expectedSessionId);
        await UpdateStagedHoldAsync(wallId, holdId, x, y, radius);
    }

    /// <inheritdoc/>
    public async Task DeleteStagedHoldInSessionAsync(
        Guid wallId, Guid? expectedSessionId, Guid holdId, StagedTwinDeletion twins = StagedTwinDeletion.Refuse)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using (var db = await dbContextFactory.CreateDbContextAsync())
        {
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);
            var open = await WallUpdateSessions.FindOpenAsync(db, wallId);
            WallUpdateSessions.EnsureIs(wallId, expectedSessionId, open, WallUpdateSessionSupersededException.ChangeRefused);
            if (open is not null)
            {
                await ReleaseTwinVerdictsAsync(db, open, holdId, twins, user.Id);
            }
        }

        await DeleteStagedHoldAsync(wallId, holdId);
    }

    /// <summary>
    /// The carry verdicts naming <paramref name="holdId"/> as their twin: refused, or re-pointed to "no twin" (same kind,
    /// unconfirmed) and saved before the delete, so the cascade finds nothing of the user's to take.
    /// </summary>
    private async Task ReleaseTwinVerdictsAsync(
        BlocwerkDbContext db, Entities.WallUpdateSession open, Guid holdId, StagedTwinDeletion twins, Guid userId)
    {
        var rows = await db.WallUpdateHoldDecisions
            .Where(d => d.SessionId == open.Id && d.Kind == WallUpdateHoldDecisionKind.Carry && d.PairedHoldId == holdId)
            .ToListAsync();
        if (rows.Count == 0)
        {
            return;
        }

        if (twins == StagedTwinDeletion.Refuse)
        {
            throw new StagedHoldIsTwinException(holdId, rows.Select(r => r.HoldId).ToList());
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows)
        {
            CarryConfirmationPolicy.Apply(row, row.CarryKind, null, confirmed: false, userId, now);
            row.UpdatedAt = now;
        }

        WallUpdateSessions.Touch(open, userId);
        await db.SaveChangesAsync();
        logger.LogInformation(
            "Staged hold {HoldId} on wall {WallId}: {Count} carry verdict(s) released from it before its delete by {UserId}",
            holdId, open.WallId, rows.Count, userId);
    }

    /// <summary>Refuses a staged edit from a tab whose update session is no longer the wall's open one.</summary>
    private async Task EnsureSessionForStagedEditAsync(Guid wallId, Guid? expectedSessionId)
    {
        if (expectedSessionId is null)
        {
            return;
        }

        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallEditorAsync(db, wallId, user.Id, CancellationToken.None);
        await WallUpdateSessions.EnsureCurrentAsync(db, wallId, expectedSessionId, message: WallUpdateSessionSupersededException.ChangeRefused);
    }
}
