// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The content-hash backfill (PR 2.2) on both providers: every blob column is hashed from its stored bytes across several
/// batches, every stored file and ladder level from its file, missing files are counted and left null, and a second
/// pass finds nothing (hashes already there, even wrong-looking ones, are never touched).
/// </summary>
public class ContentHashBackfillTests
{
    private static readonly byte[] Bytes = [9, 8, 7, 6];

    [Fact]
    public Task EveryBlobColumn_IsHashedFromItsStoredBytes_AcrossBatches() => BlobsAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task EveryBlobColumn_IsHashedFromItsStoredBytes_AcrossBatches_OnPostgres() => BlobsAsync(PostgresTestDatabase.Create());

    [Fact]
    public Task EveryStoredFileAndLadderLevel_IsHashedFromItsFile() => FilesAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task EveryStoredFileAndLadderLevel_IsHashedFromItsFile_OnPostgres() => FilesAsync(PostgresTestDatabase.Create());

    [Fact]
    public Task ASecondPass_FindsNothing_AndExistingHashesAreKept() => IdempotentAsync(null);

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task ASecondPass_FindsNothing_AndExistingHashesAreKept_OnPostgres() => IdempotentAsync(PostgresTestDatabase.Create());

    private static async Task BlobsAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        await ContentHashSeed.EveryColumnAsync(kit.H, Bytes);
        await AddPanelsAsync(kit, 7);
        await kit.NullEveryHashAsync();
        Assert.Contains(await kit.BlobHashesAsync(), p => p.Expected is not null && p.Stored is null);

        var count = await kit.Backfill().RunPassAsync(default);

        var pairs = await kit.BlobHashesAsync();
        Assert.All(pairs, p => Assert.True(ContentHash.Same(p.Expected, p.Stored), $"{p.Column} was not hashed from its bytes"));
        Assert.Equal(pairs.Count(p => p.Expected is not null), count.Stamped);
        Assert.Equal(0, count.Retry);
    }

    private static async Task FilesAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        var image = await kit.AddImageAsync([5, 5, 5]);
        var lost = await kit.AddImageAsync([6]);
        await using (var db = kit.H.CreateContext())
        {
            File.Delete(kit.H.WallImageStorage.ResolvePhysicalPath(await db.WallImages.Where(i => i.Id == lost).Select(i => i.StoragePath).SingleAsync())!);
        }

        var level = await kit.Captures.SaveAsync([7, 7], ".spz", default);
        var (splatId, textureId) = await kit.AddGeometryAsync(
            $$"""[{"splats":100,"storedPath":"{{level}}","sizeBytes":2},{"splats":200,"storedPath":"gone.spz","sizeBytes":3}]""");
        await kit.NullEveryHashAsync();

        var count = await kit.Backfill().RunPassAsync(default);

        await using var check = kit.H.CreateContext();
        Assert.Equal(SHA256.HashData([5, 5, 5]), await check.WallImages.Where(i => i.Id == image).Select(i => i.Sha256).SingleAsync());
        Assert.Null(await check.WallImages.Where(i => i.Id == lost).Select(i => i.Sha256).SingleAsync());
        var splat = await check.WallGeometrySplats.AsNoTracking().SingleAsync(s => s.Id == splatId);
        Assert.Equal(SHA256.HashData([1, 1, 1]), splat.Sha256);
        Assert.Equal(SHA256.HashData([2, 2]), splat.MobileSha256);
        var levels = SplatLodLadder.Parse(splat.LodLevelsJson);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([7, 7])), levels[0].Sha);
        Assert.Null(levels[1].Sha);
        var texture = await check.WallGeometryTextures.AsNoTracking().SingleAsync(t => t.Id == textureId);
        Assert.Equal(SHA256.HashData([3, 3, 3]), texture.Sha256);
        Assert.Equal(SHA256.HashData([4]), texture.MaskSha256);
        Assert.Equal(2, count.Missing);
        Assert.Equal(0, count.Retry);
    }

    private static async Task IdempotentAsync(PostgresTestDatabase? pg)
    {
        using var kit = new ContentHashBackfillKit(pg);
        await kit.H.SeedWallAsync(holdCount: 0);
        await AddPanelsAsync(kit, 3);
        var image = await kit.AddImageAsync([5, 5, 5]);
        await kit.NullEveryHashAsync();
        var sentinel = Enumerable.Repeat((byte)7, 32).ToArray();
        Guid kept;
        await using (var db = kit.H.CreateContext())
        {
            kept = await db.WallPanels.OrderBy(p => p.Col).Select(p => p.Id).FirstAsync();
            await db.WallPanels.Where(p => p.Id == kept).ExecuteUpdateAsync(s => s.SetProperty(p => p.PhotoSha256, sentinel));
            await db.WallImages.Where(i => i.Id == image).ExecuteUpdateAsync(s => s.SetProperty(i => i.Sha256, sentinel));
        }

        var backfill = kit.Backfill();
        Assert.True((await backfill.RunPassAsync(default)).Stamped > 0);
        var second = await backfill.RunAsync(default);

        Assert.Equal(0, second.Stamped);
        await using var check = kit.H.CreateContext();
        Assert.Equal(sentinel, await check.WallPanels.Where(p => p.Id == kept).Select(p => p.PhotoSha256).SingleAsync());
        Assert.Equal(sentinel, await check.WallImages.Where(i => i.Id == image).Select(i => i.Sha256).SingleAsync());
    }

    /// <summary>Panels with distinct photos (and one without), more than one batch's worth.</summary>
    private static async Task AddPanelsAsync(ContentHashBackfillKit kit, int count)
    {
        await using var db = kit.H.CreateContext();
        for (var i = 0; i < count; i++)
        {
            db.WallPanels.Add(new WallPanel { WallId = kit.H.WallId, Col = 10 + i, Photo = [(byte)i, 1, 2], PhotoContentType = "image/jpeg" });
        }

        db.WallPanels.Add(new WallPanel { WallId = kit.H.WallId, Col = 99 });
        await db.SaveChangesAsync();
    }
}
