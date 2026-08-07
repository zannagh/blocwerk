using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Groups logged events (attempts, hangboard/pull-up sessions) into <see cref="Activity"/> clusters.
/// Two consecutive events belong to the same activity when they are within <see cref="Gap"/> of each
/// other AND fall on the same calendar day; otherwise the later one starts a new activity. The same
/// rule drives both live logging (<see cref="ResolveActivityIdAsync"/>) and history backfill.
/// </summary>
public static class ActivityGrouping
{
    /// <summary>The inactivity gap that separates one activity from the next.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromHours(4);

    /// <summary>
    /// Whether an event at <paramref name="current"/> starts a new activity relative to the previous
    /// event at <paramref name="previous"/> (both in ascending time order). Pure — used by backfill.
    /// </summary>
    public static bool StartsNewActivity(DateTimeOffset previous, DateTimeOffset current) =>
        current - previous > Gap || previous.UtcDateTime.Date != current.UtcDateTime.Date;

    /// <summary>
    /// Finds the activity a new event at <paramref name="timestamp"/> belongs to — extending it in
    /// place — or adds a fresh one to <paramref name="db"/>, and returns its id. The caller is
    /// responsible for the surrounding <c>SaveChanges</c>; the new/updated Activity is tracked here.
    /// Robust to out-of-order arrivals (offline replay): matching is by time window, not just "latest".
    /// </summary>
    public static async Task<Guid> ResolveActivityIdAsync(
        BlocwerkDbContext db, Guid userId, DateTimeOffset timestamp, Guid? wallId)
    {
        // Activities never span a calendar day (enforced below), so a candidate is same-day and its
        // [StartedAt, LastEventAt] window must come within Gap of the timestamp. Bounds are computed
        // here so the query has no per-row date arithmetic to translate.
        var dayStart = new DateTimeOffset(timestamp.UtcDateTime.Date, TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var lo = timestamp - Gap;
        var hi = timestamp + Gap;

        var match = await db.Activities
            .Where(a => a.UserId == userId
                && a.StartedAt >= dayStart && a.StartedAt < dayEnd
                && a.StartedAt <= hi && a.LastEventAt >= lo)
            .OrderByDescending(a => a.LastEventAt)
            .FirstOrDefaultAsync();

        if (match != null)
        {
            if (timestamp > match.LastEventAt)
            {
                match.LastEventAt = timestamp;
            }

            if (timestamp < match.StartedAt)
            {
                match.StartedAt = timestamp;
            }

            if (match.WallId is null && wallId is not null)
            {
                match.WallId = wallId;
            }

            return match.Id;
        }

        var created = new Activity
        {
            UserId = userId,
            WallId = wallId,
            StartedAt = timestamp,
            LastEventAt = timestamp,
        };

        db.Activities.Add(created);
        return created.Id;
    }
}
