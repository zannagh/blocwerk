using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the staged-hold edit service methods (Add/Update/Delete) used by the whole-wall
/// big-update review's right pane. The load-bearing invariant: every method is structurally scoped
/// to the staged generation on the staged centre panel, so a live current-generation hold can never
/// be added to, moved, or deleted. Also a basic HoldGenerationLink persistence round-trip.
/// </summary>
public class StagedHoldEditTests
{
    [Fact]
    public async Task UpdateStagedHold_StagedRow_MovesAndResizes()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (_, stagedHoldId, _) = await SeedStagedCenterAsync(h);
        var service = NewService(h);

        await service.UpdateStagedHoldAsync(h.WallId, stagedHoldId, 0.7, 0.8, 0.05);

        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == stagedHoldId);
        Assert.Equal(0.7, hold.X, 3);
        Assert.Equal(0.8, hold.Y, 3);
        Assert.Equal(0.05, hold.Radius, 3);
        Assert.Equal(1, hold.Generation);
    }

    [Fact]
    public async Task UpdateStagedHold_LiveCurrentGenerationHold_Throws()
    {
        using var h = new WallTestHarness();
        var liveHolds = await h.SeedWallAsync(holdCount: 1);
        await SeedStagedCenterAsync(h);
        var liveHoldId = liveHolds[0].Id;
        var service = NewService(h);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateStagedHoldAsync(h.WallId, liveHoldId, 0.9, 0.9, 0.05));

        // The live hold must be untouched.
        await using var db = h.CreateContext();
        var live = await db.Holds.SingleAsync(x => x.Id == liveHoldId);
        Assert.Equal(0, live.Generation);
        Assert.Equal(0.1, live.X, 3);
    }

    [Fact]
    public async Task DeleteStagedHold_LiveCurrentGenerationHold_Throws_AndKeepsIt()
    {
        using var h = new WallTestHarness();
        var liveHolds = await h.SeedWallAsync(holdCount: 1);
        await SeedStagedCenterAsync(h);
        var liveHoldId = liveHolds[0].Id;
        var service = NewService(h);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteStagedHoldAsync(h.WallId, liveHoldId));

        await using var db = h.CreateContext();
        Assert.True(await db.Holds.AnyAsync(x => x.Id == liveHoldId));
    }

    [Fact]
    public async Task DeleteStagedHold_StagedRow_RemovesIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (_, stagedHoldId, _) = await SeedStagedCenterAsync(h);
        var service = NewService(h);

        await service.DeleteStagedHoldAsync(h.WallId, stagedHoldId);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == stagedHoldId));
    }

    [Fact]
    public async Task AddStagedHold_CentrePanel_CreatesStagedGenerationRow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (centerPanelId, _, _) = await SeedStagedCenterAsync(h);
        var service = NewService(h);

        var newId = await service.AddStagedHoldAsync(h.WallId, centerPanelId, 0.3, 0.4, 0.02);

        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == newId);
        Assert.Equal(1, hold.Generation);
        Assert.Equal(centerPanelId, hold.WallPanelId);
        Assert.False(hold.IsAutoDetected);
    }

    [Fact]
    public async Task AddStagedHold_NonCentrePanel_Throws()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await SeedStagedCenterAsync(h);
        var service = NewService(h);

        // A live panel that is not the staged centre panel must be refused.
        Guid livePanelId;
        await using (var db = h.CreateContext())
        {
            var panel = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Photo = [1, 2, 3], Generation = 0 };
            db.WallPanels.Add(panel);
            await db.SaveChangesAsync();
            livePanelId = panel.Id;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddStagedHoldAsync(h.WallId, livePanelId, 0.3, 0.4, 0.02));
    }

    // The manual touch-up step's semantic: adding a hold with needsReview:false and repositioning a
    // staged hold that a boulder uses are CORRECTIONS of the detector, so neither the added hold nor
    // the boulder ends up flagged for review. (Staged holds normally carry no boulder; one is attached
    // here defensively to prove the edit paths never touch a boulder's flag.)
    [Fact]
    public async Task TouchupEdits_AreFlagFree_LeaveBoulderAndAddedHoldUnflagged()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (centerPanelId, stagedHoldId, _) = await SeedStagedCenterAsync(h);
        var service = NewService(h);

        Guid boulderId;
        await using (var db = h.CreateContext())
        {
            var boulder = new Boulder
            {
                WallId = h.WallId,
                Name = "Touch-up subject",
                CreatedByUserId = h.Owner.Id,
                Generation = 1,
                NeedsReview = false,
            };
            db.Boulders.Add(boulder);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = stagedHoldId });
            await db.SaveChangesAsync();
            boulderId = boulder.Id;
        }

        // A touch-up add (correcting a detection miss) and a reposition of the boulder's staged hold.
        var addedId = await service.AddStagedHoldAsync(
            h.WallId, centerPanelId, 0.4, 0.4, 0.02, needsReview: false);
        await service.UpdateStagedHoldAsync(h.WallId, stagedHoldId, 0.6, 0.6, 0.03);

        await using (var db = h.CreateContext())
        {
            var added = await db.Holds.SingleAsync(x => x.Id == addedId);
            Assert.False(added.NeedsReview);
            Assert.Equal(1, added.Generation);

            var moved = await db.Holds.SingleAsync(x => x.Id == stagedHoldId);
            Assert.Equal(0.6, moved.X, 3);
            Assert.Equal(0.6, moved.Y, 3);

            var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
            Assert.False(boulder.NeedsReview);
        }
    }

    [Fact]
    public async Task HoldGenerationLink_RoundTrips()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (_, stagedHoldId, _) = await SeedStagedCenterAsync(h);

        Guid oldHoldId;
        Guid linkId;
        await using (var db = h.CreateContext())
        {
            var oldHold = new Hold { WallId = h.WallId, X = 0.2, Y = 0.2, Radius = 0.02, Generation = 0 };
            db.Holds.Add(oldHold);
            await db.SaveChangesAsync();
            oldHoldId = oldHold.Id;

            var link = new HoldGenerationLink
            {
                WallId = h.WallId,
                OldHoldId = oldHoldId,
                NewHoldId = stagedHoldId,
                Kind = HoldGenerationLinkKind.Changed,
                FromGeneration = 0,
                ToGeneration = 1,
            };
            db.HoldGenerationLinks.Add(link);
            await db.SaveChangesAsync();
            linkId = link.Id;
        }

        await using (var db = h.CreateContext())
        {
            var link = await db.HoldGenerationLinks.SingleAsync(l => l.Id == linkId);
            Assert.Equal(h.WallId, link.WallId);
            Assert.Equal(oldHoldId, link.OldHoldId);
            Assert.Equal(stagedHoldId, link.NewHoldId);
            Assert.Equal(HoldGenerationLinkKind.Changed, link.Kind);
            Assert.Equal(0, link.FromGeneration);
            Assert.Equal(1, link.ToGeneration);
        }
    }

    private static WallPanelService NewService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    // Stages an in-flight big update: a staged centre panel at generation 1 (CurrentGeneration + 1)
    // carrying a staged photo, plus one staged hold on it. Returns (centre panel id, staged hold id,
    // staged generation).
    private static async Task<(Guid CenterPanelId, Guid StagedHoldId, int StagedGen)> SeedStagedCenterAsync(
        WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var centerPanel = new WallPanel
        {
            WallId = h.WallId,
            Col = 0,
            Row = 0,
            Photo = null,
            StagedPhoto = [1, 2, 3],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(centerPanel);

        var stagedHold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = centerPanel.Id,
            X = 0.5,
            Y = 0.5,
            Radius = 0.02,
            Generation = 1,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(stagedHold);

        await db.SaveChangesAsync();
        return (centerPanel.Id, stagedHold.Id, 1);
    }
}
