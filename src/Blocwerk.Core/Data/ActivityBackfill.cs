using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>
/// One-time reconstruction of <see cref="Activity"/> rows from historical events that predate the
/// activity model. Idempotent: it only touches attempts/hangboard/pull-ups whose <c>ActivityId</c>
/// is still null, so re-running (or running after new unassigned rows appear) is safe and a no-op
/// once everything is grouped. Uses the same gap/day clustering as live logging
/// (<see cref="ActivityGrouping"/>).
/// </summary>
public static class ActivityBackfill
{
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, ILogger logger)
    {
        await using var db = await factory.CreateDbContextAsync();

        var attempts = await db.Attempts
            .Include(a => a.Boulder)
            .Where(a => a.ActivityId == null)
            .ToListAsync();
        var hangboard = await db.HangboardSessions.Where(h => h.ActivityId == null).ToListAsync();
        var pullups = await db.PullupSessions.Where(p => p.ActivityId == null).ToListAsync();

        if (attempts.Count == 0 && hangboard.Count == 0 && pullups.Count == 0)
        {
            return;
        }

        // Unify the three event kinds into (user, time, wall, assign-back) tuples so one clustering
        // pass covers them all. The wall is only known for boulder attempts.
        var events = new List<(Guid UserId, DateTimeOffset Timestamp, Guid? WallId, Action<Guid> Assign)>();
        events.AddRange(attempts.Select(a =>
            (a.UserId, a.Timestamp, (Guid?)a.Boulder?.WallId, new Action<Guid>(id => a.ActivityId = id))));
        events.AddRange(hangboard.Select(h =>
            (h.UserId, h.Timestamp, (Guid?)null, new Action<Guid>(id => h.ActivityId = id))));
        events.AddRange(pullups.Select(p =>
            (p.UserId, p.Timestamp, (Guid?)null, new Action<Guid>(id => p.ActivityId = id))));

        var activityCount = 0;
        foreach (var userGroup in events.GroupBy(e => e.UserId))
        {
            Activity? current = null;
            var previous = default(DateTimeOffset);

            foreach (var ev in userGroup.OrderBy(e => e.Timestamp))
            {
                if (current is null || ActivityGrouping.StartsNewActivity(previous, ev.Timestamp))
                {
                    current = new Activity
                    {
                        UserId = ev.UserId,
                        WallId = ev.WallId,
                        StartedAt = ev.Timestamp,
                        LastEventAt = ev.Timestamp,
                    };
                    db.Activities.Add(current);
                    activityCount++;
                }
                else
                {
                    current.LastEventAt = ev.Timestamp;
                    if (current.WallId is null && ev.WallId is not null)
                    {
                        current.WallId = ev.WallId;
                    }
                }

                ev.Assign(current.Id);
                previous = ev.Timestamp;
            }
        }

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Activity backfill: grouped {EventCount} events into {ActivityCount} activities.",
            attempts.Count + hangboard.Count + pullups.Count, activityCount);
    }
}
