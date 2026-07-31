using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The live climbing session: starting one supersedes any earlier open one, ending clears it,
/// and a session whose day has passed auto-closes rather than lingering forever.
/// </summary>
public class SessionServiceTests
{
    [Fact]
    public async Task StartSession_CreatesActiveSessionOnTheWall()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var started = await harness.SessionService.StartSessionAsync(harness.WallId);

        Assert.Equal(harness.WallId, started.WallId);
        Assert.Null(started.EndedAt);

        var active = await harness.SessionService.GetActiveSessionAsync();
        Assert.NotNull(active);
        Assert.Equal(started.Id, active!.Id);
        Assert.NotNull(active.Wall);
    }

    [Fact]
    public async Task StartSession_ClosesThePreviousOpenSession()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var first = await harness.SessionService.StartSessionAsync(harness.WallId);
        var second = await harness.SessionService.StartSessionAsync(harness.WallId);

        var active = await harness.SessionService.GetActiveSessionAsync();
        Assert.Equal(second.Id, active!.Id);

        await using var db = harness.CreateContext();
        var reloadedFirst = await db.ClimbingSessions.FindAsync(first.Id);
        Assert.NotNull(reloadedFirst!.EndedAt);
    }

    [Fact]
    public async Task EndSession_ClearsTheActiveSession()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        await harness.SessionService.StartSessionAsync(harness.WallId);
        await harness.SessionService.EndSessionAsync();

        Assert.Null(await harness.SessionService.GetActiveSessionAsync());
    }

    [Fact]
    public async Task GetActiveSession_AutoClosesASessionFromAPreviousDay()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        // A session left open since yesterday must not still count as live.
        var stale = new ClimbingSession
        {
            UserId = harness.Owner.Id,
            WallId = harness.WallId,
            StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        await using (var db = harness.CreateContext())
        {
            db.ClimbingSessions.Add(stale);
            await db.SaveChangesAsync();
        }

        Assert.Null(await harness.SessionService.GetActiveSessionAsync());

        await using var check = harness.CreateContext();
        var reloaded = await check.ClimbingSessions.FindAsync(stale.Id);
        Assert.NotNull(reloaded!.EndedAt);
    }

    [Fact]
    public async Task StartSession_ThrowsForAnUnknownWall()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.SessionService.StartSessionAsync(Guid.NewGuid()));
    }
}
