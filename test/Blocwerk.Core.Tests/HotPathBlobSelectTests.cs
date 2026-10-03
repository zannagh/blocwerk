using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Page-load read paths must not select the image blobs (Walls.Photo/StagedPhoto, WallPanels.Photo/StagedPhoto,
/// ~10 MB per row). Captures the SQL the services run against SQLite and asserts no select list names them.
/// </summary>
public class HotPathBlobSelectTests
{
    [Fact]
    public async Task WallPageLoad_DoesNotSelectPhotoBlobs()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulderId = await SeedBoulderAsync(h, holds);
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

    [Fact]
    public async Task ShareAndSessionReads_DoNotSelectPhotoBlobs()
    {
        using var h = new WallTestHarness();
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

    [Fact]
    public async Task BlobRead_IsDetectedByTheCapture()
    {
        using var h = new WallTestHarness();
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
}
