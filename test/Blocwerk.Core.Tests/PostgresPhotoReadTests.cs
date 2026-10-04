// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The raw-SQL photo reads (<c>length</c> and <c>substr</c> of a <c>bytea</c>) and the page-load services' selects, on
/// PostgreSQL: a stamp or a photo header comes from the stored value without the whole photo being read, and the hot
/// paths still name no photo column in their select lists.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresPhotoReadTests
{
    [PostgresFact]
    public async Task PhotoInfoStamp_ReadsLengthAndTail_OfPanelAndLegacyWallPhotos()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        var panelPhoto = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var shortPhoto = new byte[] { 9, 8, 7 };
        Guid panel, small, bare;
        await using (var db = h.CreateContext())
        {
            var panels = new[]
            {
                new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = 2, Photo = panelPhoto, PhotoRevision = 3 },
                new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Generation = 1, Photo = shortPhoto },
                new WallPanel { WallId = h.WallId, Col = 2, Row = 0, Generation = 1, Photo = null },
            };
            db.WallPanels.AddRange(panels);
            await db.SaveChangesAsync();
            (panel, small, bare) = (panels[0].Id, panels[1].Id, panels[2].Id);
        }

        await using var read = h.CreateContext();
        var stamps = await PhotoInfoStamp.LoadAsync(
            read,
            h.WallId,
            [new Wall3DPhotoKey(panel, 0), new Wall3DPhotoKey(small, 0), new Wall3DPhotoKey(bare, 0), new Wall3DPhotoKey(null, 0)],
            CancellationToken.None);

        var big = stamps[panel];
        Assert.Equal((2, 300L, 3), (big.Generation, big.Length, big.Revision));
        Assert.Equal(Convert.ToHexString(panelPhoto[^PhotoInfoStamp.TailBytes..]), big.Tail);
        Assert.Equal((3L, "090807"), (stamps[small].Length, stamps[small].Tail));
        Assert.False(stamps.ContainsKey(bare));
        var wall = stamps[h.WallId];
        Assert.Equal((3L, "010203", 0), (wall.Length, wall.Tail, wall.Revision));
    }

    [PostgresFact]
    public async Task PanelPhotoInfoLoader_ReadsSizeAndFocalLength_FromThePhotoHeaderAlone()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(holdCount: 0);
        var photo = ExifJpeg.Build(TestImages.Noise(1008, 756), focal35: 14);
        Assert.True(photo.Length > PanelPhotoInfoLoader.HeaderBytes);
        Guid panelId;
        await using (var db = h.CreateContext())
        {
            var panel = new WallPanel { WallId = h.WallId, Photo = photo, PhotoContentType = "image/jpeg", Generation = 0 };
            db.WallPanels.Add(panel);
            await db.SaveChangesAsync();
            panelId = panel.Id;
        }

        await using var read = h.CreateContext();
        var infos = await PanelPhotoInfoLoader.LoadAsync(
            read, h.WallId, [new Wall3DPhotoKey(panelId, 0), new Wall3DPhotoKey(Guid.NewGuid(), 0)], CancellationToken.None);

        var info = Assert.Single(infos).Value;
        Assert.Equal((1008, 756), (info.Width, info.Height));
        Assert.Equal(14 / 36.0 * 1008, info.FocalPx!.Value, 6);
    }

    [PostgresFact]
    public async Task WallPageLoad_DoesNotSelectPhotoBlobs()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulderId = await SeedBoulderAsync(h, holds);
        await SeedPanelAsync(h);
        var capture = new SqlCapture();
        var factory = new CapturingDbContextFactory(h.DbContextFactory.ConnectionString, capture);
        var walls = new WallService(factory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance);
        var boulders = new BoulderService(factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance);
        var panels = new WallPanelService(
            factory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance);

        await walls.GetWallAsync(h.WallId);
        await walls.GetMyWallsAsync();
        await boulders.GetBoulderAsync(boulderId);
        await boulders.GetBouldersForWallAsync(h.WallId);
        await panels.GetPanelsAsync(h.WallId);

        Assert.NotEmpty(capture.Commands);
        Assert.Empty(capture.BlobReads());
    }

    [PostgresFact]
    public async Task ShareAndSessionReads_DoNotSelectPhotoBlobs()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulderId = await SeedBoulderAsync(h, holds);
        var token = await h.WallService.GenerateShareTokenAsync(h.WallId);
        var capture = new SqlCapture();
        var factory = new CapturingDbContextFactory(h.DbContextFactory.ConnectionString, capture);
        var walls = new WallService(factory, h.CurrentUser, h.HoldDetection, h.ActivityLog, NullLogger<WallService>.Instance);
        var boulders = new BoulderService(factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance);
        var sessions = new SessionService(factory, h.CurrentUser, NullLogger<SessionService>.Instance);

        var shared = await walls.GetWallByShareTokenAsync(token);
        var sharedBoulder = await boulders.GetBoulderByShareTokenAsync(boulderId, token);
        await boulders.CreateBoulderAsync(h.WallId, "New", null, [new BoulderHoldInput(holds[0].Id)]);
        await sessions.StartSessionAsync(h.WallId);
        await sessions.GetActiveSessionAsync();

        Assert.NotNull(shared);
        Assert.Equal(2, shared!.Holds.Count);
        Assert.Equal(token, sharedBoulder!.Wall.ShareToken);
        Assert.Empty(capture.BlobReads());
    }

    [PostgresFact]
    public async Task ABlobRead_IsDetectedByTheCapture_OnNpgsqlToo()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync();
        var capture = new SqlCapture();
        var factory = new CapturingDbContextFactory(h.DbContextFactory.ConnectionString, capture);

        await using (var db = factory.CreateDbContext())
        {
            _ = db.Walls.ToList();
        }

        Assert.NotEmpty(capture.BlobReads());
    }

    private static async Task<Guid> SeedBoulderAsync(WallTestHarness h, List<Hold> holds)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder { WallId = h.WallId, Name = "B", CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        db.Set<BoulderHold>().Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holds[0].Id });
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task SeedPanelAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.WallPanels.Add(new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Photo = [1, 2, 3, 4], StagedPhoto = [5, 6] });
        await db.SaveChangesAsync();
    }
}
