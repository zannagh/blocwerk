using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the standalone cross-panel hold-linking service methods (CreateHoldLinkAsync /
/// DeleteHoldLinkAsync) that back the "link holds across panels" tool: validation, unordered-pair
/// dedupe, and idempotent break.
/// </summary>
public class HoldLinkToolTests
{
    [Fact]
    public async Task CreateHoldLink_TwoPanelHolds_CreatesSameKindLink()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (holdA, holdB, _) = await SeedTwoPanelsWithHoldsAsync(h);
        var service = NewService(h);

        await service.CreateHoldLinkAsync(h.WallId, holdA, holdB);

        await using var db = h.CreateContext();
        var link = Assert.Single(db.HoldLinks.Where(l => l.WallId == h.WallId));
        Assert.Equal(HoldLinkKind.Same, link.Kind);
    }

    [Fact]
    public async Task CreateHoldLink_ReversedDuplicate_IsNoOp()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (holdA, holdB, _) = await SeedTwoPanelsWithHoldsAsync(h);
        var service = NewService(h);

        await service.CreateHoldLinkAsync(h.WallId, holdA, holdB);

        // Same pair, opposite order — the unordered-pair dedupe must keep it a single link.
        await service.CreateHoldLinkAsync(h.WallId, holdB, holdA);

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.HoldLinks.CountAsync(l => l.WallId == h.WallId));
    }

    [Fact]
    public async Task CreateHoldLink_SelfLink_Throws()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (holdA, _, _) = await SeedTwoPanelsWithHoldsAsync(h);
        var service = NewService(h);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateHoldLinkAsync(h.WallId, holdA, holdA));
    }

    [Fact]
    public async Task CreateHoldLink_HoldsOnSamePanel_Throws()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (holdA, _, sameAsA) = await SeedTwoPanelsWithHoldsAsync(h);
        var service = NewService(h);

        // holdA and sameAsA both sit on the first panel — not a cross-panel seam link.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateHoldLinkAsync(h.WallId, holdA, sameAsA));
    }

    [Fact]
    public async Task DeleteHoldLink_RemovesPair_AndIsIdempotent()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var (holdA, holdB, _) = await SeedTwoPanelsWithHoldsAsync(h);
        var service = NewService(h);
        await service.CreateHoldLinkAsync(h.WallId, holdA, holdB);

        // Break by the reversed pair — matching is unordered.
        await service.DeleteHoldLinkAsync(h.WallId, holdB, holdA);
        await using (var db = h.CreateContext())
        {
            Assert.Equal(0, await db.HoldLinks.CountAsync(l => l.WallId == h.WallId));
        }

        // Breaking again with nothing to remove is a no-op, not an error.
        await service.DeleteHoldLinkAsync(h.WallId, holdA, holdB);
    }

    private static WallPanelService NewService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    // Two live panels at (0,0) and (1,0). Returns (holdA on panel 0, holdB on panel 1, secondHold on
    // panel 0) so tests can exercise both the cross-panel happy path and the same-panel rejection.
    private static async Task<(Guid HoldA, Guid HoldB, Guid SecondHoldOnPanelA)> SeedTwoPanelsWithHoldsAsync(
        WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var panelA = NewPanel(h.WallId, col: 0, row: 0);
        var panelB = NewPanel(h.WallId, col: 1, row: 0);
        db.WallPanels.AddRange(panelA, panelB);

        var holdA = NewHold(h.WallId, panelA.Id, 0.2, 0.2);
        var secondOnA = NewHold(h.WallId, panelA.Id, 0.4, 0.4);
        var holdB = NewHold(h.WallId, panelB.Id, 0.6, 0.6);
        db.Holds.AddRange(holdA, secondOnA, holdB);

        await db.SaveChangesAsync();
        return (holdA.Id, holdB.Id, secondOnA.Id);
    }

    private static WallPanel NewPanel(Guid wallId, int col, int row) =>
        new()
        {
            WallId = wallId,
            Col = col,
            Row = row,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = 0,
        };

    private static Hold NewHold(Guid wallId, Guid panelId, double x, double y) =>
        new()
        {
            WallId = wallId,
            WallPanelId = panelId,
            X = x,
            Y = y,
            Radius = 0.02,
            Generation = 0,
        };
}
