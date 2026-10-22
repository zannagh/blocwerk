// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The startup cleanup of old attempt entries (<see cref="AttemptActivityBackfill"/>), shared by the SQLite and the
/// PostgreSQL runs, on a small copy of production's shape: it counts the orphans first (three here), links every attempt's entry,
/// deletes the orphans (and the entry of a deleted boulder's attempt), leaves already linked entries and other types
/// alone, and is a no-op when run again.
/// </summary>
internal static class AttemptActivityBackfillChecks
{
    public static async Task LinksAndCleansAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        var t0 = new DateTimeOffset(2026, 8, 2, 18, 27, 51, TimeSpan.Zero);
        await using (var db = h.CreateContext())
        {
            var boulder = Boulder(db, h, "Kept");
            var gone = Boulder(db, h, "Gone");
            var send = Attempt(db, h, boulder, AttemptType.Send, t0);
            Entry(db, h, boulder, "Send", t0.AddMilliseconds(60));          // send's entry
            Entry(db, h, boulder, "Send", t0.AddSeconds(8));                // orphan: its send was deleted
            Entry(db, h, gone, "Flash", t0.AddSeconds(30));                 // orphan: no flash left at all
            Entry(db, h, null, "Attempt", t0.AddSeconds(40));               // orphan: its boulder was deleted
            Entry(db, h, boulder, "Nonsense", t0.AddSeconds(50));           // names no type: left alone
            var linked = Attempt(db, h, boulder, AttemptType.Flash, t0.AddMinutes(5));
            db.ActivityLog.Add(new ActivityLogEntry
            {
                WallId = h.WallId, BoulderId = boulder, UserId = h.Owner.Id, Type = ActivityType.AttemptLogged, Details = "Flash",
                AttemptId = linked, Timestamp = t0.AddMinutes(5).AddMilliseconds(80),
            });
            db.ActivityLog.Add(new ActivityLogEntry { WallId = h.WallId, UserId = h.Owner.Id, Type = ActivityType.HoldAdded });
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.Attempts.CountAsync(a => a.Id == send));
        }

        Assert.Equal(3, await OrphanCountAsync(h));

        await AttemptActivityBackfill.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);
        var after = await SnapshotAsync(h);
        await AttemptActivityBackfill.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);

        Assert.Equal(after, await SnapshotAsync(h)); // idempotent
        Assert.Equal((Linked: 2, Unlinked: 1, Total: 4), after);
    }

    // What the cleanup will delete, counted up front the way the production sizing query counts it.
    private static async Task<int> OrphanCountAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var entries = await db.ActivityLog.AsNoTracking()
            .Where(a => a.Type == ActivityType.AttemptLogged && a.AttemptId == null)
            .Select(a => new UnlinkedAttemptEntry(a.Id, a.UserId, a.BoulderId, a.Details, a.Timestamp)).ToListAsync();
        var attempts = await db.Attempts.AsNoTracking()
            .Where(a => !db.ActivityLog.Any(l => l.AttemptId == a.Id))
            .Select(a => new UnlinkedAttempt(a.Id, a.UserId, a.BoulderId, a.Type, a.Timestamp)).ToListAsync();
        return AttemptActivityMatch.Plan(entries, attempts).Orphans.Count;
    }

    private static async Task<(int Linked, int Unlinked, int Total)> SnapshotAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var entries = db.ActivityLog.Where(a => a.Type == ActivityType.AttemptLogged);
        return (await entries.CountAsync(a => a.AttemptId != null), await entries.CountAsync(a => a.AttemptId == null), await db.ActivityLog.CountAsync());
    }

    private static Guid Boulder(BlocwerkDbContext db, WallTestHarness h, string name)
    {
        var boulder = new Boulder { WallId = h.WallId, Name = name, CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        return boulder.Id;
    }

    private static Guid Attempt(BlocwerkDbContext db, WallTestHarness h, Guid boulder, AttemptType type, DateTimeOffset at)
    {
        var attempt = new Attempt { BoulderId = boulder, UserId = h.Owner.Id, Type = type, Timestamp = at };
        db.Attempts.Add(attempt);
        return attempt.Id;
    }

    private static void Entry(BlocwerkDbContext db, WallTestHarness h, Guid? boulder, string details, DateTimeOffset at) =>
        db.ActivityLog.Add(new ActivityLogEntry
        {
            WallId = h.WallId, BoulderId = boulder, UserId = h.Owner.Id, Type = ActivityType.AttemptLogged, Details = details, Timestamp = at,
        });
}
