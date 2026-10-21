// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The feed on the SQLite harness: <c>Seq</c> order with the null transaction id ignored, epochs, retention, append.</summary>
public sealed class WallFeedSqliteTests : IDisposable
{
    private static readonly FeedAudience[] Everyone = [FeedAudience.Wall, FeedAudience.Members, FeedAudience.Setters];

    private readonly SqliteConnection keepAlive;
    private readonly TestDbContextFactory factory;
    private readonly WallFeed feed;
    private readonly Guid wallId = Guid.NewGuid();

    public WallFeedSqliteTests()
    {
        var connectionString = TestDbContextFactory.IsolatedDatabase();
        keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        factory = new TestDbContextFactory(connectionString);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        feed = new WallFeed(factory.CreateDbContext, NullLogger<WallFeed>.Instance);
    }

    public void Dispose() => keepAlive.Dispose();

    [Fact]
    public async Task Read_ReturnsTheWallsRowsInSeqOrder_AndContinuesFromNext()
    {
        var start = await feed.CurrentAsync();
        await AddAsync(Row("a"), Row("b"), Row("other", wall: Guid.NewGuid()), Row("c"));

        var first = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), start, limit: 2);
        var second = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), first.Next, limit: 2);
        var third = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), second.Next, limit: 2);

        Assert.Equal(FeedEpochStore.SqliteEpoch, start.Epoch);
        Assert.Null(start.Position.TxId);
        Assert.Equal(["a", "b"], first.Changes.Select(c => c.Kind));
        Assert.Equal(["c"], second.Changes.Select(c => c.Kind));
        Assert.Empty(third.Changes);
        Assert.False(third.Resync);
    }

    [Fact]
    public async Task Read_OnlyServesTheViewersAudiences_AndUserRowsToTheirUserOnly()
    {
        var me = Guid.NewGuid();
        var start = await feed.CurrentAsync();
        await AddAsync(
            Row("wall"),
            Row("setters", audience: FeedAudience.Setters),
            Row("mine", audience: FeedAudience.User, user: me),
            Row("someone-elses", audience: FeedAudience.User, user: Guid.NewGuid()));

        var member = await feed.ReadAsync(FeedFilter.ForWall(wallId, [FeedAudience.Wall, FeedAudience.Members], me), start, 100);
        var anonymous = await feed.ReadAsync(FeedFilter.ForWall(wallId, [FeedAudience.Wall], null), start, 100);
        var own = await feed.ReadAsync(FeedFilter.ForUser(me), start, 100);

        Assert.Equal(["wall", "mine"], member.Changes.Select(c => c.Kind));
        Assert.Equal(["wall"], anonymous.Changes.Select(c => c.Kind));
        Assert.Equal(["mine"], own.Changes.Select(c => c.Kind));
    }

    [Fact]
    public async Task CursorOfAnotherEpoch_IsAnsweredWithAResync_AtTheCurrentHorizon()
    {
        await AddAsync(Row("a"));
        var current = await feed.CurrentAsync();

        var page = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), new FeedCursor(Guid.NewGuid(), FeedPosition.Start), 100);

        Assert.True(page.Resync);
        Assert.Empty(page.Changes);
        Assert.Equal(current, page.Next);
    }

    [Fact]
    public async Task Retention_DeletesOldRows_AndACursorBelowTheFloorResyncs()
    {
        var start = await feed.CurrentAsync();
        await AddAsync(Row("old", createdAt: DateTimeOffset.UtcNow.AddDays(-31)), Row("new"));

        int deleted;
        await using (var db = factory.CreateDbContext())
        {
            deleted = await WallChangeRetention.RunAsync(db, DateTimeOffset.UtcNow - WallChangeRetention.Window, CancellationToken.None);
        }

        var stale = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), start, 100);
        var fresh = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), stale.Next, 100);

        Assert.Equal(1, deleted);
        Assert.True(stale.Resync);
        Assert.False(fresh.Resync);
        Assert.Empty(fresh.Changes);
    }

    [Fact]
    public async Task Append_OutsideATransaction_Throws()
    {
        await using var db = factory.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AppendAsync(db, Row("bulk")));
    }

    [Fact]
    public async Task Append_CommitsAndRollsBackWithItsTransaction()
    {
        var start = await feed.CurrentAsync();
        await using (var db = factory.CreateDbContext())
        {
            await using var rolledBack = await db.Database.BeginTransactionAsync();
            await feed.AppendAsync(db, Row("rolled-back"));
            await rolledBack.RollbackAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            await using var committed = await db.Database.BeginTransactionAsync();
            var txId = await feed.AppendAsync(db, Row("committed", audience: FeedAudience.Members));
            await committed.CommitAsync();
            Assert.Null(txId);
        }

        var page = await feed.ReadAsync(FeedFilter.ForWall(wallId, Everyone, null), start, 100);
        var row = Assert.Single(page.Changes);
        Assert.Equal("committed", row.Kind);
        Assert.Equal(FeedAudience.Members, row.Audience);
    }

    [Fact]
    public async Task Seq_IsNeverReused_AfterTheNewestRowIsDeleted()
    {
        await AddAsync(Row("a"), Row("b"));
        long deletedSeq;
        await using (var db = factory.CreateDbContext())
        {
            deletedSeq = await db.WallChanges.MaxAsync(c => c.Seq);
            await db.WallChanges.Where(c => c.Seq == deletedSeq).ExecuteDeleteAsync();
        }

        await AddAsync(Row("c"));

        await using var check = factory.CreateDbContext();
        Assert.True(await check.WallChanges.MaxAsync(c => c.Seq) > deletedSeq, "a reused Seq would sort before cursors already past it");
    }

    private WallChange Row(string kind, Guid? wall = null, FeedAudience audience = FeedAudience.Wall, Guid? user = null, DateTimeOffset? createdAt = null) =>
        new()
        {
            WallId = wall ?? wallId,
            Kind = kind,
            EntityId = Guid.NewGuid(),
            Audience = audience,
            AudienceUserId = user,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

    private async Task AddAsync(params WallChange[] rows)
    {
        await using var db = factory.CreateDbContext();
        foreach (var row in rows)
        {
            db.WallChanges.Add(row);
            await db.SaveChangesAsync();
        }
    }
}
