// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Assets.Backfill;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What keeps the backfill safe beside live writers: a file row changed while its file is hashed is skipped and hashed
/// on the next pass, never stamped with a stale hash (the <c>xmin</c> guard on Postgres); a blob row a writer holds is
/// skipped instead of waited on; and with the real journal, feed and domain-change interceptors wired, nothing is
/// journalled, fed or published.
/// </summary>
public class ContentHashBackfillGuardTests
{
    [Fact]
    public Task AFileRowMovedToAnotherFileWhileHashing_IsNotStampedWithTheOldFilesHash() => MovedAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AFileRowMovedToAnotherFileWhileHashing_IsNotStampedWithTheOldFilesHash_OnPostgres() => MovedAsync(PostgresTestDatabase.Create());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task AFileRewrittenInPlaceWhileHashing_IsSkippedByXmin_AndHashedOnTheNextPass()
    {
        using var kit = new ContentHashBackfillKit(PostgresTestDatabase.Create());
        await kit.H.SeedWallAsync(holdCount: 0);
        var image = await kit.AddImageAsync([1]);
        await kit.NullEveryHashAsync();

        // The privacy pass's shape: the file replaced in place and the row moved, the hash left for the backfill.
        kit.Images.OnResolve = stored =>
        {
            File.WriteAllBytes(kit.H.WallImageStorage.ResolvePhysicalPath(stored)!, [2, 2]);
            using var db = kit.H.CreateContext();
            db.WallImages.Where(i => i.Id == image).ExecuteUpdate(s => s.SetProperty(i => i.SizeBytes, 2));
        };
        var backfill = kit.Backfill();

        var first = await backfill.RunPassAsync(default);
        Assert.Equal(1, first.Retry);
        Assert.Null(await ImageHashAsync(kit, image));

        Assert.Equal(1, (await backfill.RunPassAsync(default)).Stamped);
        Assert.Equal(SHA256.HashData([2, 2]), await ImageHashAsync(kit, image));
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task APanelAWriterHolds_IsSkipped_NotWaitedOn_AndHashedOnceReleased()
    {
        using var kit = new ContentHashBackfillKit(PostgresTestDatabase.Create());
        await kit.H.SeedWallAsync(holdCount: 0);
        Guid held;
        await using (var seed = kit.H.CreateContext())
        {
            var panel = new WallPanel { WallId = kit.H.WallId, Col = 5, Photo = [3, 3], PhotoContentType = "image/jpeg" };
            seed.WallPanels.Add(panel);
            await seed.SaveChangesAsync();
            held = panel.Id;
        }

        await kit.NullEveryHashAsync();
        var backfill = kit.Backfill();
        await using (var writer = kit.H.CreateContext())
        {
            await using var tx = await writer.Database.BeginTransactionAsync();
            await writer.WallPanels.Where(p => p.Id == held).ExecuteUpdateAsync(s => s.SetProperty(p => p.Row, 1));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pass = await backfill.RunPassAsync(timeout.Token);

            Assert.True(pass.Retry >= 1);
            Assert.Null(await PanelHashAsync(kit, held));
            await tx.RollbackAsync();
        }

        await backfill.RunPassAsync(default);
        Assert.Equal(SHA256.HashData([3, 3]), await PanelHashAsync(kit, held));
    }

    [Fact]
    public Task NothingIsJournalledFedOrPublished_WithTheRealInterceptorsWired() => SilentAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task NothingIsJournalledFedOrPublished_WithTheRealInterceptorsWired_OnPostgres() => SilentAsync(PostgresTestDatabase.Create());

    [Fact]
    public void ThePacer_HalvesSlowBatches_DoublesFastOnes_AndPausesAtLeastAsLongAsTheWork()
    {
        var pacer = new BackfillPacer(new ContentHashBackfillOptions { InitialBlobBatch = 8, MaxBlobBatch = 16 });
        pacer.Measure(TimeSpan.FromSeconds(1));
        Assert.Equal(4, pacer.BatchSize);
        pacer.Measure(TimeSpan.FromMilliseconds(150));
        Assert.Equal(4, pacer.BatchSize);
        pacer.Measure(TimeSpan.FromMilliseconds(10));
        pacer.Measure(TimeSpan.FromMilliseconds(10));
        pacer.Measure(TimeSpan.FromMilliseconds(10));
        Assert.Equal(16, pacer.BatchSize);
        for (var i = 0; i < 10; i++)
        {
            pacer.Measure(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, pacer.BatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(100), pacer.PauseAfter(TimeSpan.FromMilliseconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(2), pacer.PauseAfter(TimeSpan.FromSeconds(2)));
    }

    private static async Task MovedAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        var image = await kit.AddImageAsync([1]);
        var replacement = await kit.AddImageAsync([2, 2]);
        await kit.NullEveryHashAsync();
        string newPath;
        await using (var db = kit.H.CreateContext())
        {
            newPath = await db.WallImages.Where(i => i.Id == replacement).Select(i => i.StoragePath).SingleAsync();
        }

        // After the row was read and before its (old) file is hashed, the row is pointed at another file.
        kit.Images.OnResolve = _ =>
        {
            using var db = kit.H.CreateContext();
            db.WallImages.Where(i => i.Id == image).ExecuteUpdate(s => s.SetProperty(i => i.StoragePath, newPath));
        };
        var first = await kit.Backfill().RunPassAsync(default);

        Assert.Equal(1, first.Retry);
        Assert.Null(await ImageHashAsync(kit, image));
        await kit.Backfill().RunPassAsync(default);
        Assert.Equal(SHA256.HashData([2, 2]), await ImageHashAsync(kit, image));
    }

    private static async Task SilentAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        await ContentHashSeed.EveryColumnAsync(kit.H, [4, 4]);
        await kit.AddImageAsync([5, 5, 5]);
        await kit.AddGeometryAsync("[]");
        await kit.NullEveryHashAsync();
        var (journalled, fed) = await CountAsync(kit);

        // Mappers for every hashed entity that would write a row (and throw) if they were ever handed one.
        var types = new[] { typeof(Wall), typeof(WallPanel), typeof(WallReset), typeof(User), typeof(BetaVideo), typeof(WallImage), typeof(WallGeometrySplat), typeof(WallGeometryTexture) };
        var signal = new RecordingFeedSignal();
        var commits = new RecordingFeedCommits();
        var feed = new FeedInterceptor(new FeedMapperRegistry([new ThrowingMapper(types)]), signal, NullLogger<FeedInterceptor>.Instance, commits.Commits);
        var count = await kit.Backfill(new ChangeJournalInterceptor(new ChangeJournal()), feed).RunPassAsync(default);

        Assert.True(count.Stamped >= 10);
        Assert.Equal((journalled, fed), await CountAsync(kit));
        Assert.Empty(commits.Rows);
        Assert.Empty(signal.Woken);
    }

    private static async Task<(int Journalled, int Fed)> CountAsync(ContentHashBackfillKit kit)
    {
        await using var db = kit.H.CreateContext();
        return (await db.ChangeJournalEntries.CountAsync(), await db.WallChanges.CountAsync());
    }

    private static async Task<byte[]?> ImageHashAsync(ContentHashBackfillKit kit, Guid id)
    {
        await using var db = kit.H.CreateContext();
        return await db.WallImages.Where(i => i.Id == id).Select(i => i.Sha256).SingleAsync();
    }

    private static async Task<byte[]?> PanelHashAsync(ContentHashBackfillKit kit, Guid id)
    {
        await using var db = kit.H.CreateContext();
        return await db.WallPanels.Where(p => p.Id == id).Select(p => p.PhotoSha256).SingleAsync();
    }
}
