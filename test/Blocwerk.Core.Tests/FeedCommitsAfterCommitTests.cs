// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="FeedInterceptor"/> hands a save's rows to <see cref="FeedCommits"/> once they are committed, on the saving
/// thread: before a plain SaveChanges returns (its own transaction is already committed then), but only on the commit of
/// an explicit transaction (a replay, a revert), and never when it rolls back. Otherwise an open circuit re-reads the
/// wall while the replay is still uncommitted and caches the pre-replay state. Bulk-site appends follow the same rule.
/// </summary>
public sealed class FeedCommitsAfterCommitTests : IDisposable
{
    private readonly string cs = TestDbContextFactory.IsolatedDatabase();
    private readonly SqliteConnection keepAlive;
    private readonly RecordingFeedCommits commits = new();
    private readonly FeedInterceptor interceptor;

    public FeedCommitsAfterCommitTests()
    {
        keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        interceptor = Interceptor(commits);
        using var db = TestDb.Create(cs);
        db.Database.EnsureCreated();
    }

    public void Dispose() => keepAlive.Dispose();

    [Fact]
    public Task APlainSave_PublishesBeforeItReturns_Sqlite() => PlainSavePublishesAsync(cs, interceptor);

    [Fact]
    public Task ATransactionalSave_PublishesOnlyOnCommit_Sqlite() => PublishesOnCommitAsync(cs, interceptor);

    [Fact]
    public async Task ARolledBackTransaction_PublishesNothing()
    {
        var wall = await SeedWallAsync(cs);
        commits.Clear();

        await using var db = TestDb.Create(cs, interceptor);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.Holds.Add(new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        Assert.Empty(commits.Rows);
    }

    [Fact]
    public async Task ABulkAppend_PublishesOnItsCommit()
    {
        var wall = await SeedWallAsync(cs);
        commits.Clear();

        await using var db = TestDb.Create(cs, interceptor);
        await FeedBulkWrite.RunAsync(
            db, "test", () => Task.FromResult(1), _ => [FeedRows.Resync(wall, "bulk", null, DateTimeOffset.UtcNow)],
            NullLogger.Instance, CancellationToken.None);

        var row = Assert.Single(commits.Rows);
        Assert.Equal((wall, FeedKinds.WallResync), (row.WallId!.Value, row.Kind));
    }

    [Fact]
    public async Task AThrowingObserver_NeverFailsTheSave_AndTheOthersStillHearIt()
    {
        var wall = await SeedWallAsync(cs);
        using var throwing = commits.Commits.Observe(_ => throw new InvalidOperationException("a stale circuit"));
        var later = new RecordingFeedCommits();
        using var second = commits.Commits.Observe(c => later.Commits.Publish(c.Rows));
        commits.Clear();

        await using var db = TestDb.Create(cs, interceptor);
        db.Holds.Add(new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 });
        await db.SaveChangesAsync();

        Assert.Contains(commits.Rows, r => r.Kind == FeedKinds.HoldChanged);
        Assert.Contains(later.Rows, r => r.Kind == FeedKinds.HoldChanged);
    }

    [PostgresFact]
    public async Task BothRules_HoldOnPostgres()
    {
        using var pg = PostgresTestDatabase.Create();
        var recorded = new RecordingFeedCommits();
        var pgInterceptor = Interceptor(recorded);
        await PlainSavePublishesAsync(pg.ConnectionString, pgInterceptor, recorded);
        await PublishesOnCommitAsync(pg.ConnectionString, pgInterceptor, recorded);
        Assert.All(recorded.Rows, r => Assert.NotNull(r.TxId));
    }

    internal static FeedInterceptor Interceptor(RecordingFeedCommits recorded) => new(
        new FeedMapperRegistry(FeedMappers.Create()), new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance, recorded.Commits);

    private Task PlainSavePublishesAsync(string connectionString, FeedInterceptor feed) =>
        PlainSavePublishesAsync(connectionString, feed, commits);

    private Task PublishesOnCommitAsync(string connectionString, FeedInterceptor feed) =>
        PublishesOnCommitAsync(connectionString, feed, commits);

    private static async Task PlainSavePublishesAsync(string connectionString, FeedInterceptor feed, RecordingFeedCommits recorded)
    {
        var wall = await SeedWallAsync(connectionString);
        recorded.Clear();

        await using var db = TestDb.Create(connectionString, feed);
        var hold = new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();

        var row = Assert.Single(recorded.Rows);
        Assert.Equal((wall, FeedKinds.HoldChanged, hold.Id), (row.WallId!.Value, row.Kind, row.EntityId!.Value));
    }

    private static async Task PublishesOnCommitAsync(string connectionString, FeedInterceptor feed, RecordingFeedCommits recorded)
    {
        var wall = await SeedWallAsync(connectionString);
        recorded.Clear();

        await using var db = TestDb.Create(connectionString, feed);
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Holds.Add(new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 });
        await db.SaveChangesAsync();
        Assert.Empty(recorded.Rows);

        await tx.CommitAsync();
        Assert.Equal(FeedKinds.HoldChanged, Assert.Single(recorded.Rows).Kind);
    }

    private static async Task<Guid> SeedWallAsync(string connectionString)
    {
        var owner = new User { Identifier = $"{Guid.NewGuid():N}@test", DisplayName = "Owner" };
        var wall = new Wall { Name = "Wall", OwnerId = owner.Id };
        await using var db = TestDb.Create(connectionString);
        db.Users.Add(owner);
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    }
}
