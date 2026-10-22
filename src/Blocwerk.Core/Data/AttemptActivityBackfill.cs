// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// Links the <see cref="ActivityType.AttemptLogged"/> entries written before they carried their attempt's id, and deletes
/// the ones whose attempt is gone ("sent this" for an ascent somebody deleted). Idempotent, run at every start: it reads
/// only unlinked entries, so it no-ops once everything is linked or decided. The matching is
/// <see cref="AttemptActivityMatch"/>'s; an entry it cannot decide stays as it is.
/// </summary>
public static class AttemptActivityBackfill
{
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, ILogger logger)
    {
        await using var db = await factory.CreateDbContextAsync();
        var entries = await db.ActivityLog.AsNoTracking()
            .Where(a => a.Type == ActivityType.AttemptLogged && a.AttemptId == null)
            .Select(a => new UnlinkedAttemptEntry(a.Id, a.UserId, a.BoulderId, a.Details, a.Timestamp))
            .ToListAsync();
        if (entries.Count == 0)
        {
            return;
        }

        var users = entries.Select(e => e.UserId).Distinct().ToList();
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => users.Contains(a.UserId) && !db.ActivityLog.Any(l => l.AttemptId == a.Id))
            .Select(a => new UnlinkedAttempt(a.Id, a.UserId, a.BoulderId, a.Type, a.Timestamp))
            .ToListAsync();

        var plan = AttemptActivityMatch.Plan(entries, attempts);
        foreach (var (entryId, attemptId) in plan.Links)
        {
            await db.ActivityLog.Where(a => a.Id == entryId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AttemptId, attemptId));
        }

        var orphans = plan.Orphans.ToList();
        var deleted = orphans.Count == 0 ? 0 : await db.ActivityLog.Where(a => orphans.Contains(a.Id)).ExecuteDeleteAsync();
        logger.LogInformation(
            "Attempt activity backfill: {Linked} entries linked to their attempt, {Deleted} without one deleted, {Undecided} left as they are",
            plan.Links.Count, deleted, entries.Count - plan.Links.Count - orphans.Count);
    }
}
