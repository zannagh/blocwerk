// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// An attempt's activity entry ("sent this"), written and removed in the attempt's own save, so the log can never show an
/// ascent that is no longer there. Removing it through the context (not only the database's cascade) lets the feed tell
/// the lists that show it (<c>activity.removed</c>).
/// </summary>
internal static class AttemptActivityEntries
{
    /// <summary>The entry of <paramref name="attempt"/>, on the boulder's wall.</summary>
    public static ActivityLogEntry For(Attempt attempt, Guid wallId) => new()
    {
        WallId = wallId,
        BoulderId = attempt.BoulderId,
        AttemptId = attempt.Id,
        UserId = attempt.UserId,
        Type = ActivityType.AttemptLogged,
        Details = attempt.Type.ToString(),
    };

    /// <summary>Marks the entries of <paramref name="attemptId"/> deleted in <paramref name="db"/>, for its next save.</summary>
    public static async Task RemoveForAsync(BlocwerkDbContext db, Guid attemptId) =>
        db.ActivityLog.RemoveRange(await db.ActivityLog.Where(a => a.AttemptId == attemptId).ToListAsync());
}
