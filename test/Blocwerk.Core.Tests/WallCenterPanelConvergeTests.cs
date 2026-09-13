using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers <see cref="WallCenterPanelConvergence"/>, the idempotent backfill that converges every wall
/// onto the big-wall model (center panel + re-parented holds + flag) and discards any in-flight
/// single-image staged edit. The owner runs it against a pulled copy of production, so idempotency and
/// "never lose a committed hold" are the load-bearing guarantees.
/// </summary>
public class WallCenterPanelConvergeTests
{
    private static Task ConvergeAsync(WallTestHarness h) =>
        WallCenterPanelConvergence.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);

    [Fact]
    public async Task Converge_SeedsCenterPanel_ReparentsHolds_AndSetsFlag()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        await ConvergeAsync(h);

        await using var db = h.CreateContext();
        var panel = await db.WallPanels.SingleAsync(p => p.WallId == h.WallId);
        Assert.Equal(0, panel.Col);
        Assert.Equal(0, panel.Row);
        Assert.Equal(new byte[] { 1, 2, 3 }, panel.Photo);

        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.True(wall.UsesMultipleImages);

        var reparented = await db.Holds.Where(x => x.WallId == h.WallId).ToListAsync();
        Assert.Equal(holds.Count, reparented.Count);
        Assert.All(reparented, x => Assert.Equal(panel.Id, x.WallPanelId));
    }

    [Fact]
    public async Task Converge_IsIdempotent_NoDuplicateCenterPanel()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 2);

        await ConvergeAsync(h);
        await ConvergeAsync(h);

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.WallPanels.CountAsync(p => p.WallId == h.WallId && p.Col == 0 && p.Row == 0));
    }

    [Fact]
    public async Task Converge_SetsFlag_WhenPanelExistsButFlagIsOff()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        // Asymmetric legacy state: a wall the old DisableMultiImageAsync flipped off but left panels on.
        await using (var seed = h.CreateContext())
        {
            var wall = await seed.Walls.FirstAsync(w => w.Id == h.WallId);
            wall.UsesMultipleImages = false;
            seed.WallPanels.Add(new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Photo = [1, 2, 3], Generation = 0 });
            await seed.SaveChangesAsync();
        }

        await ConvergeAsync(h);

        await using var db = h.CreateContext();
        var reconciled = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.True(reconciled.UsesMultipleImages);
        Assert.Equal(1, await db.WallPanels.CountAsync(p => p.WallId == h.WallId && p.Col == 0 && p.Row == 0));
    }

    [Fact]
    public async Task Converge_DiscardsStagedEdit_ButRescuesBoulderLinkedStagedHold()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        Guid linkedStagedId;
        Guid looseStagedId;
        await using (var seed = h.CreateContext())
        {
            var seedWall = await seed.Walls.FirstAsync(w => w.Id == h.WallId);
            seedWall.StagedPhoto = [9];
            seedWall.StagedPhotoContentType = "image/jpeg";
            seedWall.StagedAt = DateTimeOffset.UtcNow;
            seedWall.StagingMode = WallStagingMode.Detected;

            var stagedGen = seedWall.CurrentGeneration + 1;
            var linked = new Hold { WallId = h.WallId, X = 0.7, Y = 0.7, Radius = 0.02, Generation = stagedGen };
            var loose = new Hold { WallId = h.WallId, X = 0.8, Y = 0.8, Radius = 0.02, Generation = stagedGen };
            seed.Holds.AddRange(linked, loose);

            var boulder = new Boulder { WallId = h.WallId, Name = "Uses staged hold", CreatedByUserId = h.Owner.Id };
            seed.Boulders.Add(boulder);
            seed.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = linked.Id });
            await seed.SaveChangesAsync();

            linkedStagedId = linked.Id;
            looseStagedId = loose.Id;
        }

        await ConvergeAsync(h);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Null(wall.StagedPhoto);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);

        // The unlinked staged hold is gone; the boulder-linked one is rescued into the live generation.
        Assert.False(await db.Holds.AnyAsync(x => x.Id == looseStagedId));
        var rescued = await db.Holds.FirstAsync(x => x.Id == linkedStagedId);
        Assert.Equal(wall.CurrentGeneration, rescued.Generation);
    }

    [Fact]
    public async Task Converge_ClearsStaleSingleImageStaging_ButLeavesPanelParentedStagedHolds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        Guid panelStagedId;
        Guid looseStagedId;
        Guid panelId;
        await using (var seed = h.CreateContext())
        {
            var staleWall = await seed.Walls.FirstAsync(w => w.Id == h.WallId);

            // Stale wall-level single-image staging fields that were never cleared.
            staleWall.StagedPhoto = [9];
            staleWall.StagedPhotoContentType = "image/jpeg";
            staleWall.StagedAt = DateTimeOffset.UtcNow;
            staleWall.StagingMode = WallStagingMode.Detected;

            var stagedGen = staleWall.CurrentGeneration + 1;

            // A legitimate in-flight big-wall staged panel with a panel-parented staged hold at gen+1,
            // alongside a null-panel single-image staged hold at the same generation.
            var panel = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, StagedPhoto = [5], Generation = stagedGen };
            seed.WallPanels.Add(panel);
            var stagedPanelHold = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.3, Y = 0.3, Radius = 0.02, Generation = stagedGen };
            var loose = new Hold { WallId = h.WallId, X = 0.6, Y = 0.6, Radius = 0.02, Generation = stagedGen };
            seed.Holds.AddRange(stagedPanelHold, loose);
            await seed.SaveChangesAsync();

            panelStagedId = stagedPanelHold.Id;
            looseStagedId = loose.Id;
            panelId = panel.Id;
        }

        await ConvergeAsync(h);

        await using var db = h.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        Assert.Null(wall.StagedPhoto);
        Assert.Equal(WallStagingMode.None, wall.StagingMode);

        // The big-wall (panel-parented) staged hold is UNTOUCHED: still at gen+1, still on its panel.
        var panelHold = await db.Holds.FirstAsync(x => x.Id == panelStagedId);
        Assert.Equal(wall.CurrentGeneration + 1, panelHold.Generation);
        Assert.Equal(panelId, panelHold.WallPanelId);

        // The null-panel single-image staged hold is still discarded.
        Assert.False(await db.Holds.AnyAsync(x => x.Id == looseStagedId));
    }

    [Fact]
    public async Task Upload_OnExistingCenterPanel_RefreshesBytes_AndParentsNewHolds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        // First converge creates the (0,0) center panel from the seeded photo and parents the seeded hold.
        await ConvergeAsync(h);

        // Re-upload with different bytes and two freshly-detected holds.
        var detected = new List<DetectedHold>
        {
            new(0.4, 0.4, 0.02, null, 0.9),
            new(0.5, 0.5, 0.02, null, 0.9),
        };
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(detected));

        await h.WallService.UploadPhotoAsync(h.WallId, [7, 7, 7], "image/png");

        await using var db = h.CreateContext();
        var center = await db.WallPanels.SingleAsync(p => p.WallId == h.WallId && p.Col == 0 && p.Row == 0);

        // Fresh bytes are served from the refreshed center panel.
        Assert.Equal(new byte[] { 7, 7, 7 }, center.Photo);
        Assert.Equal("image/png", center.PhotoContentType);

        // No current-generation hold is left orphaned from the center panel, and the new holds landed on it.
        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        var orphaned = await db.Holds.CountAsync(x =>
            x.WallId == h.WallId && x.Generation == wall.CurrentGeneration && x.WallPanelId == null);
        Assert.Equal(0, orphaned);
        Assert.True(await db.Holds.CountAsync(x => x.WallPanelId == center.Id) >= 2);
    }
}
