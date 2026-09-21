using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The panel overlays (MultiPanelViewer, PanelImageView) draw a foot hold as a smaller rounded
/// square, the way the wall editor and the hold picker do. They can only do that if the
/// <see cref="PanelHold"/> projection actually carries the category, so this covers the mapping:
/// a foot seeded on a panel must come back out of GetPanelHoldsAsync as a foot, and a hand as a hand.
/// </summary>
public class PanelHoldCategoryTests
{
    [Fact]
    public async Task GetPanelHolds_CarriesTheHoldCategoryThrough()
    {
        using var h = new WallTestHarness();
        const int generation = 2;
        await h.SeedWallAsync(holdCount: 0, generation: generation);

        Guid panelId;
        Guid footId;
        Guid handId;
        await using (var db = h.CreateContext())
        {
            var panel = new WallPanel
            {
                WallId = h.WallId,
                Col = 0,
                Row = 0,
                Photo = [1, 2, 3],
                PhotoContentType = "image/jpeg",
                Generation = generation,
            };
            db.WallPanels.Add(panel);

            var foot = NewHold(h.WallId, panel.Id, generation, 0.3, HoldCategory.Foot);
            var hand = NewHold(h.WallId, panel.Id, generation, 0.6, HoldCategory.Hand);
            db.Holds.AddRange(foot, hand);
            await db.SaveChangesAsync();

            panelId = panel.Id;
            footId = foot.Id;
            handId = hand.Id;
        }

        var service = new WallPanelService(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

        var holds = await service.GetPanelHoldsAsync(h.WallId, panelId, includeStaged: false);

        Assert.Equal(HoldCategory.Foot, Assert.Single(holds, x => x.Id == footId).Category);
        Assert.Equal(HoldCategory.Hand, Assert.Single(holds, x => x.Id == handId).Category);
    }

    private static Hold NewHold(Guid wallId, Guid panelId, int generation, double position, HoldCategory category) =>
        new()
        {
            WallId = wallId,
            WallPanelId = panelId,
            X = position,
            Y = position,
            Radius = 0.02,
            Generation = generation,
            Category = category,
        };
}
