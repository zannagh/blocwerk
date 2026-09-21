// <copyright file="WallUpdateSessionService.Confirmations.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The review-metadata half of the wall-update session service: who has confirmed which carry verdicts.
/// Deliberately its own read rather than a widening of <see cref="BigUpdateConfirmation"/>, which is the
/// promote's input and must not start carrying user names into
/// <see cref="IWallBigUpdateService.PromoteAsync"/>. The confirmation BOOL still rides on
/// <see cref="CarryoverDecision"/>, because the review already reads the decisions and needs no second
/// round-trip just to know what is done; only the attribution costs a user join and is fetched apart.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<CarryConfirmation>> GetCarryConfirmationsAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (session is null)
        {
            return [];
        }

        var rows = await db.WallUpdateHoldDecisions
            .Where(d => d.SessionId == session.Id && d.Kind == WallUpdateHoldDecisionKind.Carry && d.Confirmed)
            .ToListAsync();
        if (rows.Count == 0)
        {
            return [];
        }

        var names = await LoadConfirmerNamesAsync(db, rows);
        return rows
            .Select(d => new CarryConfirmation(
                d.HoldId,
                d.ConfirmedByUserId,
                d.ConfirmedByUserId is { } id && names.TryGetValue(id, out var name) ? name : null,
                d.ConfirmedAt ?? d.UpdatedAt))
            .ToList();
    }

    /// <summary>
    /// The effective display name of every admin who confirmed one of <paramref name="rows"/>. A user
    /// that no longer reads (deleted, anonymised) is simply absent, and the confirmation still shows.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> LoadConfirmerNamesAsync(
        BlocwerkDbContext db, List<WallUpdateHoldDecision> rows)
    {
        var ids = rows
            .Where(d => d.ConfirmedByUserId is not null)
            .Select(d => d.ConfirmedByUserId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();
        return users.ToDictionary(u => u.Id, u => u.Name);
    }
}
