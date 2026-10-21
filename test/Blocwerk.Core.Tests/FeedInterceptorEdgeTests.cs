// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The interceptor's edges (review of PR 1.2a): a transaction disposed without commit releases nothing on a later
/// commit, a suppressed save writes and wakes nothing, and a mapper failure on a boulder-scoped row still resyncs the
/// boulder's wall.
/// </summary>
public sealed class FeedInterceptorEdgeTests : IDisposable
{
    private readonly WallTestHarness harness = new();
    private readonly RecordingFeedSignal signal = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task ATransactionDisposedWithoutCommit_LeavesNoWakeForTheNextCommit()
    {
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        await using var db = Context(new ProbeHoldMapper());
        await using (await db.Database.BeginTransactionAsync())
        {
            await MoveAsync(db, hold.Id);
        }

        db.ChangeTracker.Clear();
        await using (var next = await db.Database.BeginTransactionAsync())
        {
            await next.CommitAsync();
        }

        Assert.Empty(await RowsAsync());
        Assert.Empty(signal.Woken);
    }

    [Fact]
    public async Task ASaveSuppressedAfterTheFeed_WritesAndWakesNothing_AndKeepsNoRowTracked()
    {
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        await using var db = TestDb.Create(harness.DbContextFactory.ConnectionString, Feed(new ProbeHoldMapper()), new SuppressingSaveInterceptor());

        await MoveAsync(db, hold.Id);

        Assert.Empty(await RowsAsync());
        Assert.Empty(signal.Woken);
        Assert.Empty(db.ChangeTracker.Entries<WallChange>());
    }

    [Fact]
    public async Task ASaveSuppressedBeforeTheFeed_IsNotMapped()
    {
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var counting = new CountingMapper();
        await using var db = TestDb.Create(harness.DbContextFactory.ConnectionString, new SuppressingSaveInterceptor(), Feed(counting));

        await MoveAsync(db, hold.Id);

        Assert.Equal(0, counting.Calls);
        Assert.Empty(db.ChangeTracker.Entries<WallChange>());
    }

    [Fact]
    public async Task AMapperFailureOnACommentOfAnExistingBoulder_ResyncsTheBouldersWall()
    {
        await harness.SeedWallAsync(holdCount: 0);
        var boulderId = await AddBoulderAsync();

        await using (var db = Context(new ThrowingMapper(typeof(BoulderComment))))
        {
            db.BoulderComments.Add(new BoulderComment { BoulderId = boulderId, UserId = harness.Owner.Id, Text = "nice" });
            await db.SaveChangesAsync();
        }

        AssertOneResyncOfTheWall(await RowsAsync());
    }

    [Fact]
    public async Task AMapperFailureOnACommentOfABoulderAddedInTheSameSave_ResyncsTheBouldersWall()
    {
        await harness.SeedWallAsync(holdCount: 0);

        await using (var db = Context(new ThrowingMapper(typeof(BoulderComment))))
        {
            var boulder = new Boulder { WallId = harness.WallId, Name = "B2", CreatedByUserId = harness.Owner.Id };
            db.AddRange(boulder, new BoulderComment { BoulderId = boulder.Id, UserId = harness.Owner.Id, Text = "first" });
            await db.SaveChangesAsync();
        }

        AssertOneResyncOfTheWall(await RowsAsync());
    }

    [Fact]
    public async Task AMapperFailureOnARowOfNoWall_StillSaves_AndWritesNothing()
    {
        await harness.SeedWallAsync(holdCount: 0);

        await using (var db = Context(new ThrowingMapper(typeof(User))))
        {
            var owner = await db.Users.SingleAsync(u => u.Id == harness.Owner.Id);
            owner.CustomDisplayName = "Renamed";
            await db.SaveChangesAsync();
        }

        await using var read = harness.CreateContext();
        Assert.Equal("Renamed", (await read.Users.SingleAsync(u => u.Id == harness.Owner.Id)).CustomDisplayName);
        Assert.Empty(await RowsAsync());
    }

    private void AssertOneResyncOfTheWall(List<WallChange> rows)
    {
        var row = Assert.Single(rows);
        Assert.Equal((FeedKinds.WallResync, (Guid?)harness.WallId), (row.Kind, row.WallId));
    }

    private FeedInterceptor Feed(params IFeedMapper[] mappers) =>
        new(new FeedMapperRegistry(mappers), signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());

    private BlocwerkDbContext Context(params IFeedMapper[] mappers) =>
        TestDb.Create(harness.DbContextFactory.ConnectionString, (IInterceptor)Feed(mappers));

    private async Task<Guid> AddBoulderAsync()
    {
        await using var db = harness.CreateContext();
        var boulder = new Boulder { WallId = harness.WallId, Name = "B1", CreatedByUserId = harness.Owner.Id };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task MoveAsync(BlocwerkDbContext db, Guid holdId)
    {
        var hold = await db.Holds.SingleAsync(h => h.Id == holdId);
        hold.X += 0.01;
        await db.SaveChangesAsync();
    }

    private async Task<List<WallChange>> RowsAsync()
    {
        await using var db = harness.CreateContext();
        return await db.WallChanges.AsNoTracking().OrderBy(c => c.Seq).ToListAsync();
    }
}
