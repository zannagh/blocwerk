// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A wall with one live panel photo, for the duplicate-hold review: seeds holds and boulders, builds the service.</summary>
internal sealed class HoldDuplicateScenario
{
    private readonly WallTestHarness harness;
    private readonly ChangeJournal journal = new();

    private HoldDuplicateScenario(WallTestHarness harness, Guid panelId)
    {
        this.harness = harness;
        PanelId = panelId;
    }

    public Guid PanelId { get; }

    public static async Task<HoldDuplicateScenario> CreateAsync(WallTestHarness harness)
    {
        await harness.SeedWallAsync(holdCount: 0);
        return new HoldDuplicateScenario(harness, await EnrichmentScenario.AddPanelAsync(harness, livePhoto: [1, 2, 3]));
    }

    public HoldDuplicateService Service()
    {
        var connection = ((TestDbContextFactory)harness.DbContextFactory).ConnectionString;
        var factory = new InterceptedFactory(connection, journal);
        return new HoldDuplicateService(
            factory, harness.CurrentUser, journal, new ChangeJournalReverter(factory, journal), NullLogger<HoldDuplicateService>.Instance);
    }

    public async Task<Guid> AddHoldAsync(
        double x, double y, double radius, bool auto, string? color = null, bool isVirtual = false, string? name = null, List<ShapePoint>? shape = null)
    {
        await using var db = harness.CreateContext();
        var hold = EnrichmentFakes.AutoHold(harness.WallId, x, y, radius);
        hold.WallPanelId = PanelId;
        hold.Generation = 1;
        hold.IsAutoDetected = auto;
        hold.IsVirtual = isVirtual;
        hold.Color = color;
        hold.Name = name;
        hold.ShapePoints = shape;
        hold.OutlineSource = auto ? (shape is null ? HoldOutlineSource.AutoCircle : HoldOutlineSource.AutoContour) : HoldOutlineSource.Manual;
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    public async Task<Guid> AddBoulderAsync(string name, params (Guid Hold, HoldType Type)[] holds)
    {
        await using var db = harness.CreateContext();
        var boulder = new Boulder { WallId = harness.WallId, Name = name, CreatedByUserId = harness.Owner.Id };
        db.Boulders.Add(boulder);
        foreach (var (hold, type) in holds)
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = hold, Type = type });
        }

        await db.SaveChangesAsync();
        return boulder.Id;
    }

    public async Task<Dictionary<Guid, Hold>> HoldsAsync()
    {
        await using var db = harness.CreateContext();
        return await db.Holds.AsNoTracking().ToDictionaryAsync(h => h.Id);
    }

    public async Task<List<BoulderHold>> LinksAsync(Guid boulderId)
    {
        await using var db = harness.CreateContext();
        return await db.BoulderHolds.AsNoTracking().Where(b => b.BoulderId == boulderId).ToListAsync();
    }

    public async Task<bool> NeedsReviewAsync(Guid boulderId)
    {
        await using var db = harness.CreateContext();
        return (await db.Boulders.AsNoTracking().FirstAsync(b => b.Id == boulderId)).NeedsReview;
    }

    private sealed class InterceptedFactory(string connectionString, ChangeJournal journal) : IDbContextFactory<Blocwerk.Core.Data.BlocwerkDbContext>
    {
        public Blocwerk.Core.Data.BlocwerkDbContext CreateDbContext() =>
            TestDb.Create(connectionString, new Blocwerk.Core.Data.ChangeJournalInterceptor(journal));
    }
}
