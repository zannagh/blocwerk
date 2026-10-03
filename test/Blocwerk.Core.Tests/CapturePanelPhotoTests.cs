using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Panel photos stay first-class on a marker wall: a capture never touches panels, holds or boulders,
/// and reusing a capture photo as a panel photo goes through the panel grid's own
/// <see cref="IWallPanelService.StagePanelAsync"/> with the capture's stored bytes — gated like the
/// capture (wall admin, never a kiosk) and only onto a cell the grid itself would offer.
/// </summary>
public class CapturePanelPhotoTests
{
    [Fact]
    public async Task CompletedCapture_LeavesEveryPanelHoldAndBoulderByteIdentical()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await CapturePanelSeed.SeedGridWithBoulderAsync(h);
        var before = await CapturePanelSeed.SnapshotAsync(h);

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using (var db = h.CreateContext())
        {
            Assert.Equal(WallCaptureStatus.Succeeded, (await db.WallCaptures.SingleAsync()).Status);
            Assert.True(await db.WallGeometryModels.AnyAsync(m => m.IsActive));
        }

        Assert.Equal(before, await CapturePanelSeed.SnapshotAsync(h));
    }

    [Fact]
    public async Task UseAsNewPanel_CallsStagePanelAsync_WithTheStoredStrippedBytes()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        var photo = await FirstPhotoAsync(h);
        var stored = (await s.Files.ReadAsync(photo.StoredPath, CancellationToken.None))!;
        var panels = Substitute.For<IWallPanelService>();
        panels.GetFrontierPositionsAsync(h.WallId).Returns([new PanelPosition(1, 0)]);
        var staged = new StagePanelResult(Guid.NewGuid(), []);
        panels.StagePanelAsync(default, default, default, default!, default!).ReturnsForAnyArgs(staged);

        var result = await Bridge(s, panels).StageAsNewPanelAsync(captureId, photo.Id, 1, 0);

        Assert.Same(staged, result);
        await panels.Received(1).StagePanelAsync(
            h.WallId, 1, 0, Arg.Is<byte[]>(b => b.SequenceEqual(stored)), "image/jpeg");
    }

    [Fact]
    public async Task UseAsNewPanel_EndsInExactlyTheStagedStateOfAGridAdd()
    {
        using var viaCapture = new WallTestHarness();
        using var s = new CaptureScenario(viaCapture);
        var captureId = await s.StartCaptureAsync();
        await CapturePanelSeed.SeedGridWithBoulderAsync(viaCapture);
        var photo = await FirstPhotoAsync(viaCapture);
        var stored = (await s.Files.ReadAsync(photo.StoredPath, CancellationToken.None))!;
        await Bridge(s, RealPanels(viaCapture)).StageAsNewPanelAsync(captureId, photo.Id, 0, 1);

        using var viaGrid = new WallTestHarness();
        await viaGrid.SeedWallAsync(holdCount: 0);
        await CapturePanelSeed.SeedGridWithBoulderAsync(viaGrid);
        await RealPanels(viaGrid).StagePanelAsync(viaGrid.WallId, 0, 1, stored, "image/jpeg");

        Assert.Equal(await StagedStateAsync(viaGrid), await StagedStateAsync(viaCapture));
        var (panel, _) = await StagedStateAsync(viaCapture);
        Assert.Contains($"StagedPhoto={Convert.ToHexString(stored)}", panel);
    }

    [Fact]
    public async Task UseAsNewPanel_IsRefusedForAMember_AndNeverReachesThePanelService()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        var photo = await FirstPhotoAsync(h);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var panels = Substitute.For<IWallPanelService>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Bridge(s, panels).StageAsNewPanelAsync(captureId, photo.Id, 1, 0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service.GetPhotosAsync(captureId));
        Assert.Empty(panels.ReceivedCalls());
    }

    [Fact]
    public async Task UseAsNewPanel_IsRefusedFromAKiosk_EvenOnItsOwnWall()
    {
        using var h = new WallTestHarness();
        Guid captureId;
        Guid photoId;
        using (var owner = new CaptureScenario(h))
        {
            captureId = await owner.StartCaptureAsync();
            photoId = (await FirstPhotoAsync(h)).Id;
        }

        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(_ => h.WallId);
        using var s = new CaptureScenario(h, kiosk: kiosk);
        var panels = Substitute.For<IWallPanelService>();

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => Bridge(s, panels).StageAsNewPanelAsync(captureId, photoId, 1, 0));
        Assert.Empty(panels.ReceivedCalls());
    }

    [Theory]
    [InlineData(0, 0)] // occupied by a live panel
    [InlineData(3, 0)] // empty but not next to any live panel
    public async Task UseAsNewPanel_RefusesACellTheGridDoesNotOffer(int col, int row)
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await CapturePanelSeed.SeedGridWithBoulderAsync(h);
        var photo = await FirstPhotoAsync(h);
        var frontier = await RealPanels(h).GetFrontierPositionsAsync(h.WallId);
        var panels = Substitute.For<IWallPanelService>();
        panels.GetFrontierPositionsAsync(h.WallId).Returns(frontier);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Bridge(s, panels).StageAsNewPanelAsync(captureId, photo.Id, col, row));
        Assert.Contains("not an empty cell next to a live panel", ex.Message);
        await panels.DidNotReceiveWithAnyArgs().StagePanelAsync(default, default, default, default!, default!);
    }

    [Fact]
    public async Task PrepareWallUpdate_HandsTheStoredBytesToTheirCells_AndRefusesDoubles()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        var photos = await s.Service.GetPhotosAsync(captureId);
        var bridge = Bridge(s, Substitute.For<IWallPanelService>());

        var prepared = await bridge.PrepareWallUpdateAsync(
            captureId, [new(photos[0].PhotoId, 0, 0), new(photos[1].PhotoId, 1, 0)], CancellationToken.None);

        Assert.Equal([(0, 0), (1, 0)], prepared.Select(p => (p.Photo.Col, p.Photo.Row)));
        Assert.Equal("IMG_0.jpg", prepared[0].FileName);
        await using (var db = h.CreateContext())
        {
            var path = await db.WallCapturePhotos.Where(p => p.Id == photos[1].PhotoId).Select(p => p.StoredPath).SingleAsync();
            Assert.Equal((await s.Files.ReadAsync(path, CancellationToken.None))!, prepared[1].Photo.Image);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.PrepareWallUpdateAsync(
            captureId, [new(photos[0].PhotoId, 0, 0), new(photos[1].PhotoId, 0, 0)], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.PrepareWallUpdateAsync(
            captureId, [new(photos[0].PhotoId, 0, 0), new(photos[0].PhotoId, 1, 0)], CancellationToken.None));
    }

    private static CapturePanelPhotoService Bridge(CaptureScenario s, IWallPanelService panels) =>
        new(s.Service, panels, NullLogger<CapturePanelPhotoService>.Instance);

    private static WallPanelService RealPanels(WallTestHarness h)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold> { new(0.25, 0.5, 0.03, "red", 0.9), new(0.7, 0.2, 0.02, null, 0.6) }));
        return new WallPanelService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);
    }

    private static async Task<WallCapturePhoto> FirstPhotoAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallCapturePhotos.OrderBy(p => p.Index).FirstAsync();
    }

    /// <summary>The staged panel and its holds, ids and per-harness user ids left out so two walls compare.</summary>
    private static async Task<(string Panel, string Holds)> StagedStateAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = await db.WallPanels.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.StagedPhoto != null);
        Assert.Equal(h.Owner.Id, panel.StagedByUserId);
        var holds = await db.Holds.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.WallPanelId == panel.Id).OrderBy(x => x.X).ToListAsync();
        string[] skip = ["Id", "WallId", "WallPanelId", "StagedAt", "StagedByUserId", "CreatedAt", "UpdatedAt"];
        return (
            CapturePanelSeed.Row(db, panel, skip),
            string.Join("\n", holds.Select(x => CapturePanelSeed.Row(db, x, skip))));
    }
}
