// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The catch-up and the stream with real transaction ids: the audiences, a row held back by an older open transaction
/// (served once it ends, never skipped), a quiet cursor capped below the moving horizon, and a stream resumed by
/// <c>Last-Event-ID</c>.
/// </summary>
[Trait("Db", "Postgres")]
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresFeedRouteTests
{
    [PostgresFact]
    public async Task EachCallerGetsItsAudiences()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync(PostgresTestDatabase.Create());
        var start = await f.BeginningAsync();

        Assert.Equal(["wall", "members", "setters", "owner.mine"], await KindsAsync(f, start, 4));
        f.Caller = f.Member;
        Assert.Equal(["wall", "members", "member.mine"], await KindsAsync(f, start, 3));
        f.Caller = null;
        Assert.Equal(["wall"], await KindsAsync(f, start, 1, "&token=" + FeedRouteFixture.ShareToken));
    }

    [PostgresFact]
    public async Task ARowHeldByAnOlderOpenTransaction_IsNotSkipped_AndAQuietCursorStaysCapped()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync(PostgresTestDatabase.Create());
        var caughtUp = await PageUntilAsync(f, await f.BeginningAsync(), 4);

        await using (var older = TestDb.Create(f.Harness.DbContextFactory.ConnectionString))
        {
            await using var transaction = await older.Database.BeginTransactionAsync();
            await PostgresFeedTestKit.TakeXidAsync(older);
            await f.CommitAsync(false, f.Row("held"));

            var whileOpen = FeedRouteFixture.Changes(await f.WallChangesAsync("?after=" + caughtUp.Next));
            Assert.Empty(whileOpen.Events);
            Assert.Equal(caughtUp.Next, whileOpen.Next); // capped at the stored mark, not moved to the ceiling
            await transaction.CommitAsync();
        }

        var after = await PageUntilAsync(f, caughtUp.Next, 1);
        Assert.Equal(["held"], after.Events.Select(e => e.Kind));
    }

    [PostgresFact]
    public async Task AStreamResumedWithLastEventId_WritesOnlyWhatFollows()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync(PostgresTestDatabase.Create());
        var beginning = await f.BeginningAsync();

        // The catch-up on connect reads below the cluster-wide horizon: let it pass the seeded rows first, so another
        // database's open transaction cannot hold them back past the stream's wait.
        await PostgresHorizonWait.UntilNowIsPastAsync(f.Harness.CreateContext);
        string resumeAt;
        await using (var first = FeedStreamRun.Open(f, "?after=" + beginning))
        {
            await first.Body.WaitUntilAsync(b => b.Events.Count == 4, seconds: 20);
            resumeAt = first.Body.Events[2].Cursor;
        }

        await using var second = FeedStreamRun.Open(f, lastEventId: resumeAt);
        await second.Body.WaitUntilAsync(b => b.Events.Count == 1, seconds: 20);
        await f.CommitAsync(true, f.Row("live"));
        await second.Body.WaitUntilAsync(b => b.Events.Count == 2, seconds: 20);

        Assert.Equal(["owner.mine", "live"], second.Body.Events.Select(e => e.Kind));
    }

    private static async Task<List<string>> KindsAsync(FeedRouteFixture f, string after, int expected, string extra = "") =>
        (await PageUntilAsync(f, after, expected, extra)).Events.Select(e => e.Kind).ToList();

    // The horizon is cluster-wide: another test's open transaction may hold delivery back for a moment.
    private static async Task<FeedChanges> PageUntilAsync(FeedRouteFixture f, string after, int expected, string extra = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var page = FeedRouteFixture.Changes(await f.WallChangesAsync("?after=" + after + extra));
            if (page.Events.Count >= expected || DateTime.UtcNow > deadline)
            {
                return page;
            }

            await Task.Delay(50);
        }
    }
}
