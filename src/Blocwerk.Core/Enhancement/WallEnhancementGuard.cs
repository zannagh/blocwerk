// <copyright file="WallEnhancementGuard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The two-way rule between a wall's enhancement and its updates: an enhancement does not start (or take its next step)
/// while a wall update is open, and a wall update does not start or apply while an enhancement is running. Either way a
/// run only ever writes to the generation it started on.
/// </summary>
public static class WallEnhancementGuard
{
    /// <summary>The message a wall update gets while the wall is being enhanced.</summary>
    public const string EnhancementRunning =
        "This wall is being enhanced right now. Wait until its steps are done (the review can wait), then update the wall.";

    private static readonly WallRefreshStatus[] OpenRefresh =
    [
        WallRefreshStatus.Uploading, WallRefreshStatus.Sorting, WallRefreshStatus.ReadyToStart,
        WallRefreshStatus.Running, WallRefreshStatus.ReadyToApply, WallRefreshStatus.Applying,
    ];

    /// <summary>Refuses a wall update (staging, applying, "Update panels + 3D") while the wall's enhancement runs.</summary>
    /// <param name="db">A context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>When it may go on.</returns>
    public static async Task EnsureNotRunningAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        if (await db.WallEnhancementRuns.AnyAsync(r => r.WallId == wallId && r.Status == WallEnhancementStatus.Running, ct))
        {
            throw new UserFacingException(EnhancementRunning);
        }
    }

    /// <summary>Why an enhancement cannot start or go on now: a wall update is open (it will produce a new generation anyway).</summary>
    /// <param name="db">A context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reason, or null.</returns>
    public static async Task<string?> OpenUpdateReasonAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        if (await db.WallUpdateSessions.AnyAsync(s => s.WallId == wallId && s.Status == WallUpdateSessionStatus.Open, ct))
        {
            return "A wall update is in progress. Finish or discard it first: the enhancement works on the wall as it is now.";
        }

        return await db.WallRefreshes.AnyAsync(r => r.WallId == wallId && OpenRefresh.Contains(r.Status), ct)
            ? "An \"Update panels + 3D\" is in progress. Finish or discard it first: the enhancement works on the wall as it is now."
            : null;
    }
}
