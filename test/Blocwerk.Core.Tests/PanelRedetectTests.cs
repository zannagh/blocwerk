using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Per-panel redetect must operate only on the panel's own auto-detected holds and must never orphan
/// a boulder: manual holds and any hold a boulder depends on survive it. These tests pin that
/// boulder-safety contract.
/// </summary>
public class PanelRedetectTests
{
    [Fact]
    public async Task RedetectPanel_ReplacesUnreferencedAutoHolds_KeepsManualAndBoulderReferenced()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);

        var (panelId, autoUnrefId, autoRefId, manualId) = await SeedPanelWithBoulderAsync(h);

        h.HoldDetection
            .DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(new List<DetectedHold>
            {
                new(0.5, 0.5, 0.02, null, 0.9),
                new(0.7, 0.7, 0.02, null, 0.9),
            });

        var service = CreateService(h);
        var detected = await service.RedetectPanelHoldsAsync(h.WallId, panelId);

        Assert.Equal(2, detected);

        await using var db = h.CreateContext();
        var holds = await db.Holds.Where(x => x.WallPanelId == panelId).ToListAsync();

        // The unreferenced auto hold is gone; the boulder-referenced auto hold and the manual hold stay.
        Assert.DoesNotContain(holds, x => x.Id == autoUnrefId);
        Assert.Contains(holds, x => x.Id == autoRefId);
        Assert.Contains(holds, x => x.Id == manualId);

        // Two fresh detections were added, parented to the panel and flagged for review.
        var fresh = holds.Where(x => x.IsAutoDetected && x.Id != autoRefId).ToList();
        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, x => Assert.True(x.NeedsReview));
        Assert.All(fresh, x => Assert.Equal(0, x.Generation));

        // The boulder is untouched: not made historic, its link to the kept hold intact.
        var boulder = await db.Boulders.Include(b => b.BoulderHolds).SingleAsync();
        Assert.False(boulder.IsHistoric);
        Assert.Contains(boulder.BoulderHolds, bh => bh.HoldId == autoRefId);
    }

    [Fact]
    public async Task RedetectPanel_NoLivePhoto_IsNoOp()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);

        Guid stagedPanelId;
        await using (var db = h.CreateContext())
        {
            var panel = new WallPanel
            {
                WallId = h.WallId,
                Col = 0,
                Row = 0,
                Photo = null,
                StagedPhoto = [9, 9, 9],
                StagedPhotoContentType = "image/jpeg",
                Generation = 0,
            };
            db.WallPanels.Add(panel);
            db.Holds.Add(new Hold
            {
                WallId = h.WallId,
                WallPanelId = panel.Id,
                X = 0.1,
                Y = 0.1,
                Radius = 0.02,
                IsAutoDetected = true,
                Generation = 0,
            });
            await db.SaveChangesAsync();
            stagedPanelId = panel.Id;
        }

        var service = CreateService(h);
        var detected = await service.RedetectPanelHoldsAsync(h.WallId, stagedPanelId);

        Assert.Equal(0, detected);

        await using var check = h.CreateContext();
        Assert.Equal(1, await check.Holds.CountAsync(x => x.WallPanelId == stagedPanelId));
    }

    private static WallPanelService CreateService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    /// <summary>
    /// Seeds a live centre panel carrying three holds — an unreferenced auto hold, a boulder-referenced
    /// auto hold, and a manual hold — plus a boulder that uses the referenced auto hold.
    /// </summary>
    private static async Task<(Guid PanelId, Guid AutoUnrefId, Guid AutoRefId, Guid ManualId)>
        SeedPanelWithBoulderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = 0,
            Row = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = 0,
        };
        db.WallPanels.Add(panel);

        var autoUnref = PanelHold(h.WallId, panel.Id, 0.1, isAuto: true);
        var autoRef = PanelHold(h.WallId, panel.Id, 0.2, isAuto: true);
        var manual = PanelHold(h.WallId, panel.Id, 0.3, isAuto: false);
        db.Holds.AddRange(autoUnref, autoRef, manual);

        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Test Boulder",
            CreatedByUserId = h.Owner.Id,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold
        {
            BoulderId = boulder.Id,
            HoldId = autoRef.Id,
            Type = HoldType.Normal,
        });

        await db.SaveChangesAsync();
        return (panel.Id, autoUnref.Id, autoRef.Id, manual.Id);
    }

    private static Hold PanelHold(Guid wallId, Guid panelId, double position, bool isAuto) =>
        new()
        {
            WallId = wallId,
            WallPanelId = panelId,
            X = position,
            Y = position,
            Radius = 0.02,
            IsAutoDetected = isAuto,
            Generation = 0,
        };
}
