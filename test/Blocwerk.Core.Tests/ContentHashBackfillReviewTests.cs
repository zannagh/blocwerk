// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Assets.Backfill;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The backfill's edges from review: an unreadable file (a directory in its place) is counted and passed over instead of
/// ending the run; a blob batch is capped by bytes; a file row a writer holds is skipped instead of waited on; a context
/// that loaded a wall before the backfill and saves it afterwards keeps the backfilled hash; and the options bind from
/// configuration, where <c>Enabled=false</c> switches the service off.
/// </summary>
public class ContentHashBackfillReviewTests
{
    [Fact]
    public Task AnUnreadableFile_IsCountedAndPassedOver_NotTheEndOfTheRun() => UnreadableAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AnUnreadableFile_IsCountedAndPassedOver_NotTheEndOfTheRun_OnPostgres() => UnreadableAsync(PostgresTestDatabase.Create());

    [Fact]
    public Task ABlobBatch_IsCappedByBytes_ButAlwaysTakesItsFirstRow() => ByteCapAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task ABlobBatch_IsCappedByBytes_ButAlwaysTakesItsFirstRow_OnPostgres() => ByteCapAsync(PostgresTestDatabase.Create());

    [Fact]
    public Task AWallLoadedBeforeTheBackfill_KeepsTheHashWhenSavedAfterIt() => StaleContextAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AWallLoadedBeforeTheBackfill_KeepsTheHashWhenSavedAfterIt_OnPostgres() => StaleContextAsync(PostgresTestDatabase.Create());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task AFileRowAWriterHolds_IsSkipped_NotWaitedOn()
    {
        using var kit = new ContentHashBackfillKit(PostgresTestDatabase.Create());
        await kit.H.SeedWallAsync(holdCount: 0);
        var image = await kit.AddImageAsync([1, 2]);
        await kit.NullEveryHashAsync();
        var backfill = kit.Backfill();
        await using (var writer = kit.H.CreateContext())
        {
            await using var tx = await writer.Database.BeginTransactionAsync();
            await writer.WallImages.Where(i => i.Id == image).ExecuteUpdateAsync(s => s.SetProperty(i => i.Caption, "held"));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pass = await backfill.RunPassAsync(timeout.Token);

            Assert.Equal(1, pass.Retry);
            await tx.RollbackAsync();
        }

        Assert.Equal(1, (await backfill.RunPassAsync(default)).Stamped);
        await using var check = kit.H.CreateContext();
        Assert.Equal(SHA256.HashData([1, 2]), await check.WallImages.Where(i => i.Id == image).Select(i => i.Sha256).SingleAsync());
    }

    [Fact]
    public async Task TheOptions_BindFromConfiguration_AndDisabledRunsNothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ContentHashBackfill:Enabled"] = "false", ["ContentHashBackfill:MaxBlobBatchBytes"] = "1000",
            })
            .Build();
        var bound = new ServiceCollection().AddContentHashBackfill(configuration).BuildServiceProvider()
            .GetRequiredService<ContentHashBackfillOptions>();
        Assert.False(bound.Enabled);
        Assert.Equal(1000, bound.MaxBlobBatchBytes);
        Assert.Equal(new ContentHashBackfillOptions().StartDelay, bound.StartDelay);

        using var kit = new ContentHashBackfillKit(null);
        await kit.H.SeedWallAsync(holdCount: 0);
        await kit.NullEveryHashAsync();
        var service = kit.Backfill(ContentHashBackfillKit.FastOptions with { Enabled = false });
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await using var check = kit.H.CreateContext();
        Assert.Null(await check.Walls.IgnoreQueryFilters().Where(w => w.Id == kit.H.WallId).Select(w => w.PhotoSha256).SingleAsync());
    }

    private static async Task UnreadableAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        var bad = await kit.AddImageAsync([6]);
        await using (var db = kit.H.CreateContext())
        {
            var path = kit.H.WallImageStorage.ResolvePhysicalPath(await db.WallImages.Where(i => i.Id == bad).Select(i => i.StoragePath).SingleAsync())!;
            File.Delete(path);
            Directory.CreateDirectory(path);
        }

        var level = await kit.Captures.SaveAsync([7], ".spz", default);
        Directory.CreateDirectory(kit.Captures.ResolvePhysicalPath("dir.spz")!);
        var (splatId, _) = await kit.AddGeometryAsync(
            $$"""[{"splats":100,"storedPath":"{{level}}","sizeBytes":1},{"splats":200,"storedPath":"dir.spz","sizeBytes":1}]""");
        await kit.NullEveryHashAsync();

        var count = await kit.Backfill().RunPassAsync(default);

        Assert.Equal(2, count.Missing);
        await using var check = kit.H.CreateContext();
        Assert.Null(await check.WallImages.Where(i => i.Id == bad).Select(i => i.Sha256).SingleAsync());
        var splat = await check.WallGeometrySplats.AsNoTracking().SingleAsync(s => s.Id == splatId);
        Assert.Equal(SHA256.HashData([1, 1, 1]), splat.Sha256);
        var levels = SplatLodLadder.Parse(splat.LodLevelsJson);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([7])), levels[0].Sha);
        Assert.Null(levels[1].Sha);
    }

    private static async Task ByteCapAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        await using var db = kit.H.CreateContext();
        await db.WallPanels.ExecuteDeleteAsync();
        for (var i = 0; i < 4; i++)
        {
            db.WallPanels.Add(new WallPanel { WallId = kit.H.WallId, Col = i, Photo = [(byte)i, 1, 2] });
        }

        await db.SaveChangesAsync();
        await kit.NullEveryHashAsync();
        var column = ContentHashColumn.InDatabase.Single(c => c.Entity == typeof(WallPanel) && c.Bytes == nameof(WallPanel.Photo));
        var sql = ContentHashBackfillSql.For(db, typeof(WallPanel));

        // Bytes before each row: 0, 3, 6, 9. A cap of 7 takes three rows; a cap of 1 still takes the first.
        Assert.Equal(3, await BatchCountAsync(db, sql, column, 7));
        Assert.Equal(1, await BatchCountAsync(db, sql, column, 1));
    }

    private static async Task<int> BatchCountAsync(
        Data.BlocwerkDbContext db, ContentHashBackfillSql sql, ContentHashColumn column, long cap) =>
        sql.Postgres
            ? (await db.Database.SqlQueryRaw<BackfillStampedRow>(sql.StampBlobs(column), Guid.Empty, 64, cap).ToListAsync()).Count
            : (await db.Database.SqlQueryRaw<BackfillBlobRow>(sql.SelectBlobs(column), Guid.Empty, 64, cap).ToListAsync()).Count;

    private static async Task StaleContextAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        await using (var seed = kit.H.CreateContext())
        {
            (await seed.Walls.SingleAsync(x => x.Id == kit.H.WallId)).Photo = [9, 9, 9];
            await seed.SaveChangesAsync();
        }

        await kit.NullEveryHashAsync();
        await using var circuit = kit.H.CreateContext();
        var wall = await circuit.Walls.SingleAsync(x => x.Id == kit.H.WallId);
        Assert.Null(wall.PhotoSha256);

        await kit.Backfill().RunPassAsync(default);
        wall.Name = "Renamed";
        wall.Description = "after the backfill";
        await circuit.SaveChangesAsync();

        await using var check = kit.H.CreateContext();
        Assert.Equal(SHA256.HashData([9, 9, 9]), await check.Walls.Where(x => x.Id == kit.H.WallId).Select(x => x.PhotoSha256).SingleAsync());
    }
}
