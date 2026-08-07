using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Events cluster into an <see cref="Activity"/> when they are within the inactivity gap and on the
/// same calendar day; a longer gap or a day boundary starts a new one. The same rule drives live
/// logging (<see cref="ActivityGrouping.ResolveActivityIdAsync"/>) and history backfill.
/// </summary>
public class ActivityGroupingTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void StartsNewActivity_SplitsOnlyBeyondTheGap(int hoursLater, bool expected)
    {
        Assert.Equal(expected, ActivityGrouping.StartsNewActivity(Base, Base.AddHours(hoursLater)));
    }

    [Fact]
    public void StartsNewActivity_SplitsOnDayBoundaryEvenWithinGap()
    {
        var lateNight = new DateTimeOffset(2026, 1, 15, 23, 0, 0, TimeSpan.Zero);
        var afterMidnight = new DateTimeOffset(2026, 1, 16, 0, 30, 0, TimeSpan.Zero); // 1.5h later, next day

        Assert.True(ActivityGrouping.StartsNewActivity(lateNight, afterMidnight));
    }

    [Fact]
    public async Task Resolve_ClustersCloseEventsAndSplitsDistantOnes()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var first = await ResolveAsync(harness, Base, harness.WallId);
        var withinGap = await ResolveAsync(harness, Base.AddHours(1), null);
        var beyondGap = await ResolveAsync(harness, Base.AddHours(9), null);

        Assert.Equal(first, withinGap);
        Assert.NotEqual(first, beyondGap);

        await using var db = harness.CreateContext();
        Assert.Equal(2, await db.Activities.CountAsync());

        // The clustered activity spans both of its events, and inherits the wall from the attempt.
        var clustered = await db.Activities.FirstAsync(a => a.Id == first);
        Assert.Equal(Base, clustered.StartedAt);
        Assert.Equal(Base.AddHours(1), clustered.LastEventAt);
        Assert.Equal(harness.WallId, clustered.WallId);
    }

    [Fact]
    public async Task Resolve_AttachesAnEarlierOutOfOrderEvent()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var created = await ResolveAsync(harness, Base, null);
        var earlier = await ResolveAsync(harness, Base.AddHours(-1), null); // arrives late, same session

        Assert.Equal(created, earlier);

        await using var db = harness.CreateContext();
        var activity = await db.Activities.FirstAsync(a => a.Id == created);
        Assert.Equal(Base.AddHours(-1), activity.StartedAt); // start pulled back to the earlier event
    }

    [Fact]
    public async Task Backfill_GroupsUnassignedEventsAndIsIdempotent()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var boulderId = await SeedBoulderAsync(harness);

        // Two attempts 1h apart (one session) + a hangboard 6h later (a second session), all ungrouped.
        await using (var db = harness.CreateContext())
        {
            db.Attempts.Add(new Attempt { BoulderId = boulderId, UserId = harness.Owner.Id, Type = AttemptType.Send, Timestamp = Base });
            db.Attempts.Add(new Attempt { BoulderId = boulderId, UserId = harness.Owner.Id, Type = AttemptType.Attempt, Timestamp = Base.AddHours(1) });
            db.HangboardSessions.Add(new HangboardSession { UserId = harness.Owner.Id, EdgeSizeMm = 20, Sets = 3, Duration = TimeSpan.FromSeconds(10), Timestamp = Base.AddHours(6) });
            await db.SaveChangesAsync();
        }

        await ActivityBackfill.RunIfNeededAsync(harness.DbContextFactory, NullLogger.Instance);

        await using (var db = harness.CreateContext())
        {
            Assert.Equal(2, await db.Activities.CountAsync());
            Assert.False(await db.Attempts.AnyAsync(a => a.ActivityId == null));
            Assert.False(await db.HangboardSessions.AnyAsync(h => h.ActivityId == null));

            // The two nearby attempts share one activity; the hangboard is on its own.
            var attemptActivities = await db.Attempts.Select(a => a.ActivityId).Distinct().ToListAsync();
            Assert.Single(attemptActivities);
        }

        // Re-running assigns nothing new.
        await ActivityBackfill.RunIfNeededAsync(harness.DbContextFactory, NullLogger.Instance);
        await using (var db = harness.CreateContext())
        {
            Assert.Equal(2, await db.Activities.CountAsync());
        }
    }

    private static async Task<Guid> ResolveAsync(WallTestHarness harness, DateTimeOffset ts, Guid? wallId)
    {
        await using var db = harness.CreateContext();
        var id = await ActivityGrouping.ResolveActivityIdAsync(db, harness.Owner.Id, ts, wallId);
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedBoulderAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        var boulder = new Boulder { WallId = harness.WallId, Name = "B", CreatedByUserId = harness.Owner.Id };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
