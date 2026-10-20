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
        "This wall is being enhanced right now. Wait until its steps are done (the review can wait), or cancel the run in the wall's settings, then update the wall.";

    /// <summary>A Running run whose heartbeat is older than this has a job that is gone or hung: it no longer blocks the wall.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private static readonly WallRefreshStatus[] OpenRefresh =
    [
        WallRefreshStatus.Uploading, WallRefreshStatus.Sorting, WallRefreshStatus.ReadyToStart,
        WallRefreshStatus.Running, WallRefreshStatus.ReadyToApply, WallRefreshStatus.Applying,
    ];

    /// <summary>
    /// Refuses a wall update (staging, applying, "Update panels + 3D") while the wall's enhancement runs. A run with a
    /// stale heartbeat does not block: it is marked as stopped (it says so on the wall's settings, where it can be
    /// discarded) and the update goes ahead.
    /// </summary>
    /// <param name="db">A context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>When it may go on.</returns>
    public static async Task EnsureNotRunningAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct = default)
    {
        var running = await db.WallEnhancementRuns.Where(r => r.WallId == wallId && r.Status == WallEnhancementStatus.Running).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (running.Any(r => !IsStale(r, now)))
        {
            throw new UserFacingException(EnhancementRunning);
        }

        foreach (var run in running)
        {
            var lastSign = run.HeartbeatAt ?? run.UpdatedAt;
            run.Status = WallEnhancementStatus.Failed;
            run.CurrentStep = null;
            run.Error = $"Stopped: no sign of its job since {lastSign:u}, and a wall update went ahead.";
            run.UpdatedAt = now;
        }

        if (running.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Whether a Running run's job is gone or hung (no heartbeat for <see cref="StaleAfter"/>).</summary>
    /// <param name="run">The run.</param>
    /// <param name="now">The clock.</param>
    /// <returns>True when stale.</returns>
    public static bool IsStale(WallEnhancementRun run, DateTimeOffset now) =>
        now - (run.HeartbeatAt ?? run.UpdatedAt) > StaleAfter;

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
