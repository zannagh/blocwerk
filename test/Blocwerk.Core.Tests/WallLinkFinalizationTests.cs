using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Cross-panel links are finalized" is stored as the generation it was declared at, not as a flag,
/// so anything that moves the wall on has to re-open the question. These tests pin the two ways that
/// happens: silently, because a promote bumps the wall past the stored generation, and explicitly,
/// because a panel went live with a new photo within the same generation.
/// </summary>
public class WallLinkFinalizationTests
{
    [Fact]
    public async Task FinalizeLinks_ThenGenerationBump_ReadsAsNotFinalized()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 3);

        var service = CreateService(h);
        await service.SetHoldLinksFinalizedAsync(h.WallId, true);

        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            Assert.Equal(3, wall.LinksFinalizedGeneration);
            Assert.Equal(wall.CurrentGeneration, wall.LinksFinalizedGeneration);
        }

        // What a promote does, without going through the promote service: the stored value stays put
        // and simply falls behind, which is the whole self-invalidation mechanism.
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            wall.CurrentGeneration = 4;
            await db.SaveChangesAsync();
        }

        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            Assert.Equal(3, wall.LinksFinalizedGeneration);
            Assert.NotEqual(wall.CurrentGeneration, wall.LinksFinalizedGeneration);
        }
    }

    [Fact]
    public async Task UnfinalizeLinks_ClearsTheStoredGeneration()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 1);

        var service = CreateService(h);
        await service.SetHoldLinksFinalizedAsync(h.WallId, true);
        await service.SetHoldLinksFinalizedAsync(h.WallId, false);

        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        Assert.Null(wall.LinksFinalizedGeneration);
    }

    [Fact]
    public async Task ConfirmPanel_ClearsFinalizedLinks()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);

        var service = CreateService(h);
        await service.SetHoldLinksFinalizedAsync(h.WallId, true);

        Guid panelId;
        await using (var db = h.CreateContext())
        {
            var panel = new WallPanel
            {
                WallId = h.WallId,
                Col = 1,
                Row = 0,
                StagedPhoto = [4, 5, 6],
                StagedPhotoContentType = "image/jpeg",
                StagedAt = DateTimeOffset.UtcNow,
                Generation = 0,
            };
            db.WallPanels.Add(panel);
            await db.SaveChangesAsync();
            panelId = panel.Id;
        }

        await service.ConfirmPanelAsync(h.WallId, panelId, [], []);

        await using (var db = h.CreateContext())
        {
            // A new photo is live on the wall, so the editor has to look at cross-panel linking again.
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            Assert.Null(wall.LinksFinalizedGeneration);
        }
    }

    private static WallPanelService CreateService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);
}
