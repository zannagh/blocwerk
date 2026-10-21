// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="FeedScope"/> and <see cref="FeedCollapse"/>: inside a scope every save writes one <c>wall.resync</c> per wall
/// it touched (across saves and contexts of the same flow), a closed scope stops collapsing work that inherited it, and a
/// save of more than 200 rows for one wall collapses on its own.
/// </summary>
public sealed class FeedScopeTests : IDisposable
{
    private readonly WallTestHarness harness = new();
    private readonly RecordingFeedSignal signal = new();
    private readonly FeedInterceptor interceptor;

    public FeedScopeTests()
    {
        interceptor = new FeedInterceptor(new FeedMapperRegistry([new ProbeHoldMapper()]), signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
    }

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task InsideAScope_EachSaveWritesOneResyncPerWall_AcrossSavesAndContexts()
    {
        var holds = await harness.SeedWallAsync(holdCount: 3);
        using (FeedScope.Begin(harness.WallId, "promote"))
        {
            await using (var first = Context())
            {
                await MoveAllAsync(first, holds.Select(h => h.Id)); // save 1: three holds
                await MoveAllAsync(first, [holds[0].Id]); // save 2, same context
            }

            await using var second = Context();
            await MoveAllAsync(second, [holds[1].Id]); // save 3, another context of the same flow
        }

        var rows = await RowsAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal((FeedKinds.WallResync, harness.WallId), (r.Kind, r.WallId)));
        Assert.All(rows, r => Assert.Contains("\"reason\":\"promote\"", r.DataJson));
    }

    [Fact]
    public async Task ASaveInAScope_ThatTouchesTwoWalls_ResyncsBoth()
    {
        var holds = await harness.SeedWallAsync(holdCount: 1);
        var other = await AddWallWithHoldAsync();
        using (FeedScope.Begin(harness.WallId, "reset"))
        {
            await using var db = Context();
            await MoveAllAsync(db, [holds[0].Id, other.HoldId]);
        }

        var rows = await RowsAsync();
        Assert.Equal(new[] { harness.WallId, other.WallId }.Order(), rows.Select(r => r.WallId!.Value).Order());
        Assert.All(rows, r => Assert.Equal(FeedKinds.WallResync, r.Kind));
    }

    [Fact]
    public async Task WorkThatInheritedAClosedScope_WritesFineGrainedRowsAgain()
    {
        var holds = await harness.SeedWallAsync(holdCount: 1);
        var release = new TaskCompletionSource();
        Task leftover;
        using (FeedScope.Begin(harness.WallId, "staging"))
        {
            // Inherits the ambient scope, but runs its save only after the operation has ended.
            leftover = Task.Run(async () =>
            {
                await release.Task;
                await using var db = Context();
                await MoveAllAsync(db, [holds[0].Id]);
            });
        }

        release.SetResult();
        await leftover;

        Assert.Equal(FeedKinds.HoldChanged, Assert.Single(await RowsAsync()).Kind);
        Assert.Null(FeedScope.Active);
    }

    [Fact]
    public void ANestedScope_RestoresTheOuterOneWhenItEnds()
    {
        using var outer = FeedScope.Begin(harness.WallId, "outer");
        using (FeedScope.Begin(harness.WallId, "inner"))
        {
            Assert.Equal("inner", FeedScope.Active!.Label);
        }

        Assert.Equal("outer", FeedScope.Active!.Label);
    }

    [Fact]
    public void OutsideAScope_MoreThan200RowsForOneWall_CollapseToOneResync_AndOtherWallsKeepTheirRows()
    {
        var big = Guid.NewGuid();
        var small = Guid.NewGuid();
        var rows = Enumerable.Range(0, FeedCollapse.MaxRowsPerWall + 1).Select(_ => Row(big))
            .Prepend(Row(small)).Append(Row(small)).Append(new WallChange { Kind = FeedKinds.UserProfile }).ToList();

        var collapsed = FeedCollapse.Apply(rows, scope: null, DateTimeOffset.UtcNow);

        var expected = new (Guid?, string)[]
        {
            (small, FeedKinds.HoldChanged), (big, FeedKinds.WallResync), (small, FeedKinds.HoldChanged), (null, FeedKinds.UserProfile),
        };
        Assert.Equal(expected, collapsed.Select(r => (r.WallId, r.Kind)));
        Assert.Contains(FeedCollapse.BulkReason, collapsed[1].DataJson);
    }

    [Fact]
    public void OutsideAScope_200RowsForOneWall_AreKept()
    {
        var wall = Guid.NewGuid();
        var rows = Enumerable.Range(0, FeedCollapse.MaxRowsPerWall).Select(_ => Row(wall)).ToList();

        Assert.Same(rows, FeedCollapse.Apply(rows, scope: null, DateTimeOffset.UtcNow));
    }

    private static WallChange Row(Guid wallId) => new() { WallId = wallId, Kind = FeedKinds.HoldChanged, EntityId = Guid.NewGuid() };

    private BlocwerkDbContext Context() => TestDb.Create(harness.DbContextFactory.ConnectionString, interceptor);

    private static async Task MoveAllAsync(BlocwerkDbContext db, IEnumerable<Guid> holdIds)
    {
        var ids = holdIds.ToList();
        foreach (var hold in await db.Holds.Where(h => ids.Contains(h.Id)).ToListAsync())
        {
            hold.X += 0.01;
        }

        await db.SaveChangesAsync();
    }

    private async Task<(Guid WallId, Guid HoldId)> AddWallWithHoldAsync()
    {
        await using var db = harness.CreateContext();
        var wall = new Wall { Name = "Other", OwnerId = harness.Owner.Id, Photo = [1], PhotoContentType = "image/jpeg" };
        var hold = new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02 };
        db.AddRange(wall, hold);
        await db.SaveChangesAsync();
        return (wall.Id, hold.Id);
    }

    private async Task<List<WallChange>> RowsAsync()
    {
        await using var db = harness.CreateContext();
        return await db.WallChanges.AsNoTracking().OrderBy(c => c.Seq).ToListAsync();
    }
}
