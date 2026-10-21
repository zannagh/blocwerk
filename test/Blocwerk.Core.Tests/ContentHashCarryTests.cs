// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The stamp's edges: a hand-set hash over unchanged bytes is put back (so a wall read without its photos keeps its hash,
/// and nothing is written, journalled or fed for it); a promote carries the staged hash instead of hashing again, but
/// only for the staged array itself; and an unreadable stored upload leaves a null hash instead of failing the upload.
/// </summary>
public class ContentHashCarryTests
{
    private static readonly byte[] WallPhoto = [1, 2, 3];

    [Fact]
    public Task AHandSetHash_IsPutBack_AndNeitherJournalledNorFed() => HandSetHashAsync(new WallTestHarness());

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public Task AHandSetHash_IsPutBack_AndNeitherJournalledNorFed_OnPostgres() =>
        HandSetHashAsync(new WallTestHarness(PostgresTestDatabase.Create()));

    [Fact]
    public async Task APromote_CarriesTheStagedHash_InsteadOfHashingAgain()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panelId = await StagePanelAsync(h, [4, 5, 6]);

        // A sentinel in place of the stored staged hash: seeing it on the live column proves it was carried, not recomputed.
        var sentinel = Enumerable.Repeat((byte)7, 32).ToArray();
        await using (var db = h.CreateContext())
        {
            await db.WallPanels.Where(p => p.Id == panelId).ExecuteUpdateAsync(s => s.SetProperty(p => p.StagedPhotoSha256, sentinel));
        }

        await PanelService(h).ConfirmPanelAsync(h.WallId, panelId, [], []);

        await using var check = h.CreateContext();
        var panel = await check.WallPanels.AsNoTracking().SingleAsync(p => p.Id == panelId);
        Assert.Equal(new byte[] { 4, 5, 6 }, panel.Photo);
        Assert.Equal(sentinel, panel.PhotoSha256);
        Assert.Null(panel.StagedPhotoSha256);
    }

    [Fact]
    public async Task ABigUpdatePromote_CarriesEveryPanelsStagedHash_AndTheCentresOntoTheWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var centreHash = Enumerable.Repeat((byte)7, 32).ToArray();
        var neighbourHash = Enumerable.Repeat((byte)8, 32).ToArray();
        var centre = await StagePanelAsync(h, [1, 2, 3, 4], centreHash, col: 0, generation: 1);
        var neighbour = await StagePanelAsync(h, [4, 5, 6], neighbourHash, col: 1, generation: 1);
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallBigUpdateService>.Instance);

        await service.PromoteAsync(h.WallId, new BigUpdateConfirmation([], [], [], []));

        await using var check = h.CreateContext();
        Assert.Equal(centreHash, await check.WallPanels.Where(p => p.Id == centre).Select(p => p.PhotoSha256).SingleAsync());
        Assert.Equal(neighbourHash, await check.WallPanels.Where(p => p.Id == neighbour).Select(p => p.PhotoSha256).SingleAsync());
        var wall = await check.Walls.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == h.WallId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, wall.Photo);
        Assert.Equal(centreHash, wall.PhotoSha256);
    }

    [Fact]
    public async Task ACarry_HoldsOnlyForTheStagedArray_AndOnlyWhileTheStagedBytesAreAsLoaded()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var sentinel = Enumerable.Repeat((byte)7, 32).ToArray();

        // Other bytes assigned after the carry: hashed.
        var first = await StagePanelAsync(h, [4, 5, 6], sentinel);
        await using (var db = h.CreateContext())
        {
            var panel = await db.WallPanels.SingleAsync(p => p.Id == first);
            panel.Photo = panel.StagedPhoto;
            db.CarryStagedPhotoHash(panel);
            panel.Photo = [8, 8];
            await db.SaveChangesAsync();
        }

        // The staged bytes changed in this context before the promote: their stored hash is stale, so hashed.
        var second = await StagePanelAsync(h, [4, 5, 6], sentinel, col: 2);
        await using (var db = h.CreateContext())
        {
            var panel = await db.WallPanels.SingleAsync(p => p.Id == second);
            panel.StagedPhoto = [9, 9];
            panel.Photo = panel.StagedPhoto;
            db.CarryStagedPhotoHash(panel);
            await db.SaveChangesAsync();
        }

        await using var check = h.CreateContext();
        Assert.Equal(SHA256.HashData([8, 8]), await check.WallPanels.Where(p => p.Id == first).Select(p => p.PhotoSha256).SingleAsync());
        Assert.Equal(SHA256.HashData([9, 9]), await check.WallPanels.Where(p => p.Id == second).Select(p => p.PhotoSha256).SingleAsync());
    }

    [Fact]
    public async Task AnUploadWhoseFileCannotBeRead_IsStillRecorded_WithoutAHash()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var directory = Directory.CreateTempSubdirectory("blocwerk-unreadable-").FullName;
        var storage = Substitute.For<IWallImageStorage>();
        storage.ResolvePhysicalPath(Arg.Any<string>()).Returns(directory);
        var service = new WallImageService(h.DbContextFactory, storage, NullLogger<WallImageService>.Instance);
        try
        {
            var image = await service.RecordImageAsync(h.WallId, "a.jpg", "image/jpeg", 4);

            await using var db = h.CreateContext();
            Assert.Null(await db.WallImages.Where(i => i.Id == image.Id).Select(i => i.Sha256).SingleAsync());
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    private static async Task HandSetHashAsync(WallTestHarness harness)
    {
        using var h = harness;
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == h.Owner.Id)).AvatarImage = [5, 5];
            await db.SaveChangesAsync();
        }

        var signal = new RecordingFeedSignal();
        var feed = new FeedInterceptor(new FeedMapperRegistry([new ThrowingMapper(typeof(Wall)), new ProbeUserMapper()]), signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        await using (var db = TestDb.Create(h.DbContextFactory.ConnectionString, new ChangeJournalInterceptor(new ChangeJournal()), feed))
        {
            await db.AttachVisibleWallsWithoutPhotosAsync();
            var wall = db.ChangeTracker.Entries<Wall>().Single().Entity;
            Assert.Null(wall.Photo);
            wall.PhotoSha256 = new byte[32];
            (await db.Users.SingleAsync(u => u.Id == h.Owner.Id)).AvatarSha256 = new byte[32];
            Assert.Equal(0, await db.SaveChangesAsync());

            wall.Name = "Renamed";
            Assert.True(await db.SaveChangesAsync() > 0);
        }

        await using var check = h.CreateContext();
        var stored = await check.Walls.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == h.WallId);
        Assert.Equal("Renamed", stored.Name);
        Assert.Equal(WallPhoto, stored.Photo);
        Assert.Equal(SHA256.HashData(WallPhoto), stored.PhotoSha256);
        Assert.Equal(SHA256.HashData([5, 5]), await check.Users.Where(u => u.Id == h.Owner.Id).Select(u => u.AvatarSha256).SingleAsync());
        var journalled = await check.ChangeJournalEntries.Where(e => e.EntityType == nameof(Wall)).ToListAsync();
        Assert.All(journalled, e => Assert.DoesNotContain("Sha256", e.AfterJson ?? string.Empty));
        Assert.Single(journalled, e => e.AfterJson!.Contains("Renamed"));

        // Only the rename reached the feed (the probe wall mapper writes a resync for each wall it is handed); no user row.
        var rows = await check.WallChanges.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(h.WallId, rows[0].WallId);
    }

    private static async Task<Guid> StagePanelAsync(WallTestHarness h, byte[] staged, byte[]? storedHash = null, int col = 1, int generation = 0)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel { WallId = h.WallId, Col = col, StagedPhoto = staged, StagedPhotoContentType = "image/jpeg", StagedAt = DateTimeOffset.UtcNow, Generation = generation };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        if (storedHash is not null)
        {
            await db.WallPanels.Where(p => p.Id == panel.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.StagedPhotoSha256, storedHash));
        }

        return panel.Id;
    }

    private static WallPanelService PanelService(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance);
}
