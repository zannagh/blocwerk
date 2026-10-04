// <copyright file="WallUpdateSessionService.Defaults.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Writing a whole set of default decisions atomically, unless the user changed the session meanwhile.</summary>
public partial class WallUpdateSessionService
{
    /// <inheritdoc/>
    public async Task<DateTimeOffset?> SaveDefaultDecisionsAsync(Guid wallId, DefaultDecisions decisions, DateTimeOffset? onlyIfUnchangedSince = null)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await RequireOpenAsync(db, wallId);

        // Row lock without a change, then the committed state: a user write before it shows here, one after it waits.
        await db.WallUpdateSessions.Where(s => s.Id == session.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, x => x.UpdatedAt));
        var updatedAt = await db.WallUpdateSessions.AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.UpdatedAt).FirstAsync();
        if (onlyIfUnchangedSince is { } since && updatedAt > since)
        {
            logger.LogInformation("Wall update session {SessionId}: changed by a person since {Since}; default decisions not written", session.Id, since);
            return null;
        }

        await db.Entry(session).ReloadAsync();
        await WriteCarryOutcomeAsync(db, session, decisions.Carryover, decisions.AcceptedNewCentreHoldIds, decisions.RemovedNewCentreHoldIds);
        foreach (var set in decisions.Neighbours)
        {
            await WriteNeighbourLinkSetAsync(db, session, set);
        }

        if (decisions.Exceptions is { } cards)
        {
            await ReplaceExceptionsAsync(db, session, cards);
        }

        WallUpdateSessions.MovePhase(session, decisions.Phase, 0, user.Id);
        await db.SaveChangesAsync();

        // Read back inside the transaction (still under the row lock): exactly the stamp these decisions carry, as the
        // database stores it. A person's write after the commit is strictly later, so "changed since" stays exact.
        var written = await db.WallUpdateSessions.AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.UpdatedAt).FirstAsync();
        await transaction.CommitAsync();
        return written;
    }
}
