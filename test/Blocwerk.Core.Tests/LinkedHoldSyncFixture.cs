// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>A wall with panels and linked holds, plus journalling services over the same database (SQLite or Postgres).</summary>
public sealed class LinkedHoldSyncFixture : IDisposable
{
    private readonly WallTestHarness harness;
    private readonly ChangeJournal journal = new();
    private readonly IDbContextFactory<BlocwerkDbContext> factory;

    private LinkedHoldSyncFixture(WallTestHarness harness)
    {
        this.harness = harness;
        var connection = ((TestDbContextFactory)harness.DbContextFactory).ConnectionString;
        factory = new Factory(connection, journal);
    }

    public Guid WallId => harness.WallId;

    public IDbContextFactory<BlocwerkDbContext> Journalling => factory;

    public ChangeJournal Journal => journal;

    public static async Task<LinkedHoldSyncFixture> CreateAsync(PostgresTestDatabase? postgres = null)
    {
        var harness = new WallTestHarness(postgres);
        await harness.SeedWallAsync(holdCount: 0);
        return new LinkedHoldSyncFixture(harness);
    }

    public async Task<Guid> AddPanelAsync(int col, int row)
    {
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = WallId, Col = col, Row = row, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", Generation = 0 };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    public async Task<Guid> AddHoldAsync(Guid panelId, Action<Hold>? configure = null, double x = 0.5, double y = 0.5)
    {
        await using var db = harness.CreateContext();
        var hold = new Hold { WallId = WallId, WallPanelId = panelId, X = x, Y = y, Radius = 0.02, Generation = 0 };
        configure?.Invoke(hold);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    public async Task LinkAsync(Guid a, Guid b)
    {
        await using var db = harness.CreateContext();
        db.HoldLinks.Add(new HoldLink { WallId = WallId, HoldAId = a, HoldBId = b });
        await db.SaveChangesAsync();
    }

    public async Task<Hold> GetAsync(Guid id)
    {
        await using var db = harness.CreateContext();
        return await db.Holds.AsNoTracking().FirstAsync(h => h.Id == id);
    }

    public async Task SetAsync(Guid id, Action<Hold> change)
    {
        await using var db = harness.CreateContext();
        change(await db.Holds.FirstAsync(h => h.Id == id));
        await db.SaveChangesAsync();
    }

    public async Task<Wall> GetWallAsync()
    {
        await using var db = harness.CreateContext();
        return await db.Walls.AsNoTracking().FirstAsync(w => w.Id == WallId);
    }

    public async Task<List<ChangeJournalBatch>> BatchesAsync(string label)
    {
        await using var db = harness.CreateContext();
        return await db.ChangeJournalBatches.AsNoTracking().Where(b => b.Label == label).ToListAsync();
    }

    public LinkedHoldSyncService SyncService()
    {
        var kiosk = Substitute.For<Blocwerk.Core.Abstractions.IKioskContext>();
        return new LinkedHoldSyncService(
            factory, harness.CurrentUser, journal, new ChangeJournalReverter(factory, journal), NullLogger<LinkedHoldSyncService>.Instance, kiosk);
    }

    public WallService EditService() =>
        new(factory, harness.CurrentUser, harness.HoldDetection, harness.ActivityLog, NullLogger<WallService>.Instance, changeJournal: journal);

    public WallPanelService PanelService() =>
        new(factory, harness.CurrentUser, harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance);

    public Task<ChangeJournalRevertResult> RevertAsync(Guid batchId) =>
        new ChangeJournalReverter(factory, journal).RevertBatchAsync(batchId, force: false, CancellationToken.None);

    public async Task EditAsync(Guid holdId, Func<HoldEdit, HoldEdit> change)
    {
        var hold = await GetAsync(holdId);
        var edit = change(new HoldEdit { X = hold.X, Y = hold.Y, Radius = hold.Radius });
        await EditService().UpdateHoldAsync(holdId, edit);
    }

    public void Dispose() => harness.Dispose();

    private sealed class Factory(string connection, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
    {
        public BlocwerkDbContext CreateDbContext() => TestDb.Create(connection, new ChangeJournalInterceptor(journal));
    }
}
