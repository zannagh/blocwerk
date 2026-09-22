using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the batched lineage read behind the editor's changed-holds overlay
/// (<see cref="WallPanelService.GetCarriedHoldIdsAsync"/>). Generations are immutable, so a hold
/// carried through a wall update is a FRESH row at the new generation and nothing on the entity tells
/// it apart from a hold placed for the first time — the <see cref="HoldGenerationLink"/> is the only
/// record, and this read is what surfaces it to the client.
/// </summary>
public class PanelCarriedHoldLineageTests
{
    [Fact]
    public async Task ReturnsOnlyTheHoldThatContinuesAnEarlierOne()
    {
        // The production shape: one gen-2 hold carried onto the gen-3 panel next to one placed fresh.
        // Both look identical on the entity; only the link separates them.
        using var h = new WallTestHarness();
        var w = await SeedPanelWithOneCarriedAndOneNewHoldAsync(h);

        var carried = await NewPanelService(h).GetCarriedHoldIdsAsync(w.WallId, [w.CarriedHoldId, w.NewHoldId]);

        Assert.Equal(w.CarriedHoldId, Assert.Single(carried));
        Assert.DoesNotContain(w.NewHoldId, carried);
    }

    [Fact]
    public async Task WallWithNoLineageAtAll_ReturnsAnEmptySet()
    {
        // A wall that has never been through a generation-carrying update. The caller MUST be able to
        // see "no history here" and exclude nothing, rather than treat every hold as new.
        using var h = new WallTestHarness();
        var w = await SeedPanelWithOneCarriedAndOneNewHoldAsync(h, withLineage: false);

        var carried = await NewPanelService(h).GetCarriedHoldIdsAsync(w.WallId, [w.CarriedHoldId, w.NewHoldId]);

        Assert.Empty(carried);
    }

    [Fact]
    public async Task NoHoldIds_ReturnsAnEmptySetWithoutQuerying()
    {
        using var h = new WallTestHarness();
        var w = await SeedPanelWithOneCarriedAndOneNewHoldAsync(h);

        Assert.Empty(await NewPanelService(h).GetCarriedHoldIdsAsync(w.WallId, []));
    }

    private static WallPanelService NewPanelService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    /// <summary>
    /// A live gen-3 panel holding two holds: one linked back to a gen-2 predecessor (carried) and one
    /// with no lineage row (brand new). <paramref name="withLineage"/> false drops the link so the
    /// same wall stands in for one that has never been updated.
    /// </summary>
    private static async Task<PanelFixture> SeedPanelWithOneCarriedAndOneNewHoldAsync(
        WallTestHarness h,
        bool withLineage = true)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic",
            OwnerId = h.Owner.Id,
            CurrentGeneration = 3,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var panel = new WallPanel
        {
            WallId = wall.Id,
            Col = 0,
            Row = 0,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = 3,
        };
        db.WallPanels.Add(panel);

        var ancestor = new Hold { WallId = wall.Id, X = 0.30, Y = 0.30, Radius = 0.02, Generation = 2 };
        var carried = new Hold { WallId = wall.Id, WallPanelId = panel.Id, X = 0.31, Y = 0.31, Radius = 0.02, Generation = 3 };
        var placed = new Hold { WallId = wall.Id, WallPanelId = panel.Id, X = 0.70, Y = 0.40, Radius = 0.02, Generation = 3 };
        db.Holds.AddRange(ancestor, carried, placed);

        if (withLineage)
        {
            db.HoldGenerationLinks.Add(new HoldGenerationLink
            {
                WallId = wall.Id,
                OldHoldId = ancestor.Id,
                NewHoldId = carried.Id,
                Kind = HoldGenerationLinkKind.Same,
                FromGeneration = 2,
                ToGeneration = 3,
            });
        }

        await db.SaveChangesAsync();
        return new PanelFixture(wall.Id, panel.Id, carried.Id, placed.Id);
    }

    private sealed record PanelFixture(Guid WallId, Guid PanelId, Guid CarriedHoldId, Guid NewHoldId);
}
