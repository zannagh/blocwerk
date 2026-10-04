// <copyright file="HoldMoveJournalTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>BoulderHoldMoves</c> are journalled with the rest of the wall update, so reverting a promote removes them together
/// with the memberships it removed and restores the boulder, and replaying puts them back.
/// </summary>
public sealed class HoldMoveJournalTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string cs;
    private readonly ChangeJournal journal;
    private readonly FuncFactory factory;
    private readonly ICurrentUserService currentUser;
    private readonly User owner = new() { Identifier = "owner@test", DisplayName = "Owner" };
    private Guid wallId;
    private Guid boulderId;
    private Guid oldMover;
    private Guid oldStayer;
    private Guid newMover;
    private Guid newStayer;

    public HoldMoveJournalTests()
    {
        cs = TestDbContextFactory.IsolatedDatabase();
        connection = new SqliteConnection(cs);
        connection.Open();
        journal = new ChangeJournal(CreateContext);
        factory = new FuncFactory(CreateContext);
        using (var db = CreateContext())
        {
            db.Database.EnsureCreated();
        }

        currentUser = Substitute.For<ICurrentUserService>();
        currentUser.GetCurrentUserAsync().Returns(_ => Task.FromResult(owner));
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task APromotedMove_IsJournalled_AndRevertingTheBatchRemovesItAndRestoresTheBoulder()
    {
        await SeedAndPromoteAsync();
        Guid batchId;
        await using (var db = CreateContext())
        {
            batchId = (await db.ChangeJournalBatches.SingleAsync(b => b.ScopeId == wallId)).Id;
            Assert.True(await db.ChangeJournalEntries.AnyAsync(e => e.BatchId == batchId && e.EntityType == nameof(BoulderHoldMove)));
            Assert.Single(await db.BoulderHoldMoves.ToListAsync());
            Assert.Single(await db.BoulderHolds.Where(b => b.BoulderId == boulderId).ToListAsync());
        }

        var result = await new ChangeJournalReverter(factory, journal).RevertBatchAsync(batchId);

        Assert.True(result.Reverted);
        Assert.Empty(result.Conflicts);
        await using var check = CreateContext();
        Assert.Empty(await check.BoulderHoldMoves.ToListAsync());
        Assert.Equal(
            new[] { oldMover, oldStayer }.Order(),
            (await check.BoulderHolds.Where(b => b.BoulderId == boulderId).Select(b => b.HoldId).ToListAsync()).Order());
        var boulder = await check.Boulders.SingleAsync(b => b.Id == boulderId);
        Assert.Equal(2, boulder.Generation);
        Assert.False(boulder.NeedsReview);
    }

    private async Task SeedAndPromoteAsync()
    {
        wallId = Guid.NewGuid();
        boulderId = Guid.NewGuid();
        (oldMover, oldStayer, newMover, newStayer) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var extra = Enumerable.Range(0, 3).Select(_ => (Old: Guid.NewGuid(), New: Guid.NewGuid())).ToList();
        await using (var db = CreateContext())
        {
            db.Users.Add(new User { Id = owner.Id, Identifier = owner.Identifier, DisplayName = owner.DisplayName });
            db.Walls.Add(new Wall
            {
                Id = wallId, Name = "Moves", OwnerId = owner.Id, CurrentGeneration = 2, Photo = [1], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
            });
            db.WallMembers.Add(new WallMember { WallId = wallId, UserId = owner.Id, Role = WallRole.Admin });
            var live = new WallPanel { WallId = wallId, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
            db.WallPanels.Add(live);
            db.Holds.AddRange(Placed(oldMover, live.Id, 2, 1000, 0.5), Placed(oldStayer, live.Id, 2, 1200, 0.2));
            db.Holds.AddRange(extra.Select((e, i) => Placed(e.Old, live.Id, 2, 1050 + (100.0 * i), 0.1)));
            db.Boulders.Add(new Boulder { Id = boulderId, WallId = wallId, Name = "Route", CreatedByUserId = owner.Id, Generation = 2 });
            db.BoulderHolds.AddRange(
                new BoulderHold { BoulderId = boulderId, HoldId = oldMover, Type = HoldType.Start },
                new BoulderHold { BoulderId = boulderId, HoldId = oldStayer });
            await db.SaveChangesAsync();
        }

        using (journal.BeginWallUpdateBatch(wallId))
        {
            await using var db = CreateContext();
            var staged = new WallPanel { WallId = wallId, Col = 0, Row = 0, StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3 };
            db.WallPanels.Add(staged);
            db.Holds.AddRange(Placed(newMover, staged.Id, 3, 1340, 0.5), Placed(newStayer, staged.Id, 3, 1200, 0.2));
            db.Holds.AddRange(extra.Select((e, i) => Placed(e.New, staged.Id, 3, 1050 + (100.0 * i), 0.1)));
            await db.SaveChangesAsync();
        }

        var service = new WallBigUpdateService(
            factory, currentUser, Substitute.For<IHoldDetectionService>(), Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance, journal, moveOptions: HoldMovePromoteTests.Loose);
        await service.PromoteAsync(
            wallId,
            new BigUpdateConfirmation(
                [
                    new CarryoverDecision(oldMover, CarryKind.Changed, newMover),
                    new CarryoverDecision(oldStayer, CarryKind.Carried, newStayer),
                    .. extra.Select(e => new CarryoverDecision(e.Old, CarryKind.Carried, e.New)),
                ],
                [], [], []));
    }

    private Hold Placed(Guid id, Guid panelId, int gen, double a, double x) => new()
    {
        Id = id, WallId = wallId, WallPanelId = panelId, Generation = gen, X = x, Y = 0.5, Radius = 0.02, FacetId = "0", PlaneAMm = a, PlaneBMm = 1000,
        IsAutoDetected = gen == 3, NeedsReview = false,
    };

    private BlocwerkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(cs)
            .AddInterceptors(new ChangeJournalInterceptor(journal))
            .Options;
        var db = new SqliteBlocwerkDbContext(options);
        db.CurrentUserId = Guid.Empty;
        return db;
    }

    private sealed class FuncFactory(Func<BlocwerkDbContext> create) : IDbContextFactory<BlocwerkDbContext>
    {
        public BlocwerkDbContext CreateDbContext() => create();
    }
}
