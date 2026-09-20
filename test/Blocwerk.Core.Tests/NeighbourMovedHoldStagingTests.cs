using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The phantom-duplicate fix. The neighbour-overlap step's "moved → add the missing hold" action runs
/// against a STAGED panel of the in-flight update but reached the live per-panel add path, which stamped
/// the LIVE generation. Promote then treated that fresh row as an old gen-N hold on an updated panel and
/// gave it a successor clone plus a lineage link — a duplicate of a hold the user had just placed. The
/// add path now stamps the staged generation when the target panel is staged.
/// </summary>
public class NeighbourMovedHoldStagingTests
{
    [Fact]
    public async Task AddPanelHold_OnAStagedPanel_IsStampedWithTheStagedGeneration()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());
        var neighbourPanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 1, 0);

        var holdId = await WallUpdateSessionFixture.Panels(h)
            .AddPanelHoldAsync(h.WallId, neighbourPanelId, 0.3, 0.3, 0.02);

        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        Assert.Equal(1, hold.Generation);
    }

    [Fact]
    public async Task AddPanelHold_OnALivePanel_StillUsesTheLiveGeneration()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        Guid livePanelId;
        await using (var db = h.CreateContext())
        {
            var panel = new Blocwerk.Core.Entities.WallPanel
            {
                WallId = h.WallId,
                Col = 0,
                Row = 0,
                Photo = [1, 2, 3],
                PhotoContentType = "image/jpeg",
                Generation = 0,
            };
            db.WallPanels.Add(panel);
            await db.SaveChangesAsync();
            livePanelId = panel.Id;
        }

        var holdId = await WallUpdateSessionFixture.Panels(h)
            .AddPanelHoldAsync(h.WallId, livePanelId, 0.3, 0.3, 0.02);

        await using var check = h.CreateContext();
        Assert.Equal(0, (await check.Holds.SingleAsync(x => x.Id == holdId)).Generation);
    }

    [Fact]
    public async Task MovedAddThenPromote_LeavesExactlyOneHoldAndNoLineageClone()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());
        var centrePanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var neighbourPanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 1, 0);
        var centreTwin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centrePanelId, 1);

        // The user places the hold the matcher missed on the STAGED neighbour panel, then links it as moved.
        var placed = await WallUpdateSessionFixture.Panels(h)
            .AddPanelHoldAsync(h.WallId, neighbourPanelId, 0.3, 0.3, 0.02);

        await service.PromoteAsync(h.WallId, new BigUpdateConfirmation(
            [new CarryoverDecision(old.Id, CarryKind.Carried, centreTwin)],
            [],
            [],
            [new NeighbourLinkSet(neighbourPanelId, [new ConfirmedLink(centreTwin, placed, Moved: true)], [])]));

        await using var db = h.CreateContext();
        var onNeighbour = await db.Holds
            .Where(x => x.WallPanelId == neighbourPanelId)
            .ToListAsync();
        Assert.Single(onNeighbour);
        Assert.Equal(placed, onNeighbour[0].Id);
        Assert.Equal(1, onNeighbour[0].Generation);

        // No successor clone was minted for the freshly placed hold.
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == placed));
        Assert.True(await db.HoldLinks.AnyAsync(l => l.HoldAId == centreTwin && l.HoldBId == placed));
    }
}
