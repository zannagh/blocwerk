// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="DomainChangeInterceptor"/> publishes a save's changes once they are committed: right after a plain
/// SaveChanges (its own transaction is already committed then), but only on the commit of an explicit transaction (a
/// replay, a revert), and never when that transaction rolls back. Otherwise an open circuit re-reads the wall while the
/// replay is still uncommitted and caches the pre-replay state.
/// </summary>
public sealed class DomainChangeAfterCommitTests : IDisposable
{
    private readonly string cs = TestDbContextFactory.IsolatedDatabase();
    private readonly SqliteConnection keepAlive;
    private readonly RecordingDomainChangeNotifier notifier = new();
    private readonly DomainChangeInterceptor interceptor;

    public DomainChangeAfterCommitTests()
    {
        keepAlive = new SqliteConnection(cs);
        keepAlive.Open();
        interceptor = new DomainChangeInterceptor(notifier);
        using var db = TestDb.Create(cs);
        db.Database.EnsureCreated();
    }

    public void Dispose() => keepAlive.Dispose();

    [Fact]
    public Task APlainSave_PublishesRightAway_Sqlite() => PlainSavePublishesAsync(cs);

    [Fact]
    public Task ATransactionalSave_PublishesOnlyOnCommit_Sqlite() => PublishesOnCommitAsync(cs);

    [Fact]
    public async Task ARolledBackTransaction_PublishesNothing()
    {
        var wall = await SeedWallAsync(cs);
        notifier.Changes.Clear();

        await using var db = TestDb.Create(cs, interceptor);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.Holds.Add(new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 });
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        Assert.Empty(notifier.Changes);
    }

    [PostgresFact]
    public async Task BothRules_HoldOnPostgres()
    {
        using var pg = PostgresTestDatabase.Create();
        await PlainSavePublishesAsync(pg.ConnectionString);
        await PublishesOnCommitAsync(pg.ConnectionString);
    }

    private async Task PlainSavePublishesAsync(string connectionString)
    {
        var wall = await SeedWallAsync(connectionString);
        notifier.Changes.Clear();

        await using var db = TestDb.Create(connectionString, interceptor);
        db.Holds.Add(new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 });
        await db.SaveChangesAsync();

        Assert.Contains(new DomainChange(DomainChangeScope.Wall, wall, Guid.Empty), notifier.Changes);
    }

    private async Task PublishesOnCommitAsync(string connectionString)
    {
        var wall = await SeedWallAsync(connectionString);
        notifier.Changes.Clear();

        await using var db = TestDb.Create(connectionString, interceptor);
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Holds.Add(new Hold { WallId = wall, X = 0.5, Y = 0.5, Radius = 0.02 });
        await db.SaveChangesAsync();
        Assert.Empty(notifier.Changes);

        await tx.CommitAsync();
        Assert.Equal([new DomainChange(DomainChangeScope.Wall, wall, Guid.Empty)], notifier.Changes);
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
