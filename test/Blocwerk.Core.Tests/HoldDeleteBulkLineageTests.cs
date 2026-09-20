using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The BULK hold-delete paths, which <see cref="HoldDeleteLineageTests"/> (single-hold) does not reach.
/// Two things are pinned here. First, lineage: a set delete has to tombstone the dying ends of
/// <see cref="HoldGenerationLink"/> the same way the single delete does, including the case where BOTH
/// ends die in the SAME pass (the row is removed outright, a branch two sequential deletes never take).
/// Second, and more important, boulder safety: the wall-wide clean-ups delete by a FILTER, not by a
/// reviewed list, so they must keep failing loudly on the Restrict FK when a live boulder uses one of
/// the matched holds — detaching there would silently retire real routes for a clean-up action.
/// </summary>
public class HoldDeleteBulkLineageTests
{
    [Fact]
    public async Task RedetectPanel_RemovingAnAutoHoldThatAPromoteCarriedForward_TombstonesItsLineage()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 1);

        var predecessorId = await SeedWallHoldAsync(h, x: 0.9, generation: 0);
        var (panelId, autoId) = await SeedLivePanelWithAutoHoldAsync(h, generation: 1);

        // The exact case PrepareRemovableAsync's comment names: an auto-detected hold a promote carried
        // forward from an earlier generation, so it owns lineage even though no boulder uses it. Before
        // the lineage rows were handled, this redetect took the whole SaveChanges down on the Restrict FK.
        await SeedGenerationLinkAsync(h, predecessorId, autoId, fromGeneration: 0, toGeneration: 1);

        h.HoldDetection
            .DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(new List<DetectedHold> { new(0.5, 0.5, 0.02, null, 0.9) });

        var detected = await CreatePanelService(h).RedetectPanelHoldsAsync(h.WallId, panelId);

        Assert.Equal(1, detected);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == autoId));

        // The predecessor still records what became of it; only the dying end was nulled.
        var link = Assert.Single(await db.HoldGenerationLinks.AsNoTracking().ToListAsync());
        Assert.Equal(predecessorId, link.OldHoldId);
        Assert.Null(link.NewHoldId);
        Assert.Equal(0, link.FromGeneration);
        Assert.Equal(1, link.ToGeneration);
    }

    [Fact]
    public async Task ClearAutoDetectedHolds_DeletingBOTHLineageEndsInOnePass_RemovesTheRow()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);

        // Both ends are auto-detected at the current generation, so ONE call matches both and the row
        // loses both ends in a single preparation — the two-nulls-then-Remove branch, which sequential
        // deletes (Update, then Delete) never exercise.
        var oldId = await SeedWallHoldAsync(h, x: 0.2, generation: 0, isAutoDetected: true);
        var newId = await SeedWallHoldAsync(h, x: 0.3, generation: 0, isAutoDetected: true);
        await SeedGenerationLinkAsync(h, oldId, newId, fromGeneration: 0, toGeneration: 0);

        await h.WallService.ClearAutoDetectedHoldsAsync(h.WallId);

        await using var db = h.CreateContext();
        Assert.Empty(await db.Holds.AsNoTracking().Where(x => x.WallId == h.WallId).ToListAsync());
        Assert.Empty(await db.HoldGenerationLinks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ClearAutoDetectedHolds_WithABoulderOnAMatchedHold_FailsLoudly_AndRetiresNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);

        // Auto-detected holds are exactly what users build boulders from, and this clean-up matches
        // every one of them with no boulder filter. Detaching here would retire live routes silently,
        // so the Restrict FK must still stop the whole delete.
        var holdId = await SeedWallHoldAsync(h, x: 0.2, generation: 0, isAutoDetected: true);
        var boulderId = await AttachBoulderAsync(h, holdId);

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => h.WallService.ClearAutoDetectedHoldsAsync(h.WallId));

        await AssertNothingWasDestroyedAsync(h, holdId, boulderId);
    }

    [Fact]
    public async Task CleanOutsideBorder_WithABoulderOnAnOutsideHold_FailsLoudly_AndRetiresNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);
        await SetBorderAsync(h);

        // Well outside the border below, and carrying a live boulder. The user must move the border or
        // the hold; the clean-up may not decide on its own that the route is over.
        var holdId = await SeedWallHoldAsync(h, x: 0.95, generation: 0);
        var boulderId = await AttachBoulderAsync(h, holdId);

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => h.WallService.CleanOutsideBorderAsync(h.WallId));

        await AssertNothingWasDestroyedAsync(h, holdId, boulderId);
    }

    [Fact]
    public async Task CleanOutsideBorder_RemovesAnUnreferencedOutsideHold_AndTombstonesItsLineage()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 0);
        await SetBorderAsync(h);

        var insideId = await SeedWallHoldAsync(h, x: 0.5, generation: 0);
        var outsideId = await SeedWallHoldAsync(h, x: 0.95, generation: 0);
        await SeedGenerationLinkAsync(h, insideId, outsideId, fromGeneration: 0, toGeneration: 0);

        var removed = await h.WallService.CleanOutsideBorderAsync(h.WallId);

        Assert.Equal(1, removed);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == outsideId));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == insideId));

        var link = Assert.Single(await db.HoldGenerationLinks.AsNoTracking().ToListAsync());
        Assert.Equal(insideId, link.OldHoldId);
        Assert.Null(link.NewHoldId);
    }

    [Fact]
    public async Task Merge_WithATombstonedLineageRowOnTheDuplicate_RepointsItOntoTheSurvivor()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21);

        // Its predecessor was deleted earlier, so this row is already half tombstoned. The dedupe must
        // not treat a NULL end as a collidable key: the row is re-pointed, not dropped.
        await SeedGenerationLinkAsync(h, null, duplicateId, fromGeneration: 0, toGeneration: 1);

        // A tombstone the survivor ALREADY owns. It is indistinguishable from the one above once both
        // point at the survivor, and nothing collapses them — the unique index is filtered to exclude
        // NULL ends, so duplicate tombstones simply accumulate. Pinned here so the documented
        // behaviour is a decision rather than a surprise.
        await SeedGenerationLinkAsync(h, null, survivorId, fromGeneration: 0, toGeneration: 1);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AsNoTracking().AnyAsync(x => x.Id == duplicateId));

        var links = await db.HoldGenerationLinks.AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Null(l.OldHoldId));
        Assert.All(links, l => Assert.Equal(survivorId, l.NewHoldId));
    }

    private static async Task AssertNothingWasDestroyedAsync(WallTestHarness h, Guid holdId, Guid boulderId)
    {
        await using var db = h.CreateContext();
        Assert.True(await db.Holds.AsNoTracking().AnyAsync(x => x.Id == holdId));
        Assert.True(await db.BoulderHolds.AsNoTracking().AnyAsync(bh => bh.HoldId == holdId));

        var boulder = await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == boulderId);
        Assert.False(boulder.IsHistoric);
    }

    private static WallPanelService CreatePanelService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    private static async Task<Guid> SeedWallHoldAsync(
        WallTestHarness h, double x, int generation, bool isAutoDetected = false)
    {
        await using var db = h.CreateContext();

        var hold = new Hold
        {
            WallId = h.WallId,
            X = x,
            Y = x,
            Radius = 0.02,
            Generation = generation,
            IsAutoDetected = isAutoDetected,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static async Task<Guid> SeedVirtualHoldAsync(WallTestHarness h, double x)
    {
        await using var db = h.CreateContext();

        var hold = new Hold
        {
            WallId = h.WallId,
            X = x,
            Y = x,
            Radius = 0.02,
            IsVirtual = true,
            Generation = 0,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static async Task<(Guid PanelId, Guid AutoHoldId)> SeedLivePanelWithAutoHoldAsync(
        WallTestHarness h, int generation)
    {
        await using var db = h.CreateContext();

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

        var hold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = 0.3,
            Y = 0.3,
            Radius = 0.02,
            Generation = generation,
            IsAutoDetected = true,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return (panel.Id, hold.Id);
    }

    /// <summary>A square border around the centre of the wall; a hold at 0.95 sits well outside it.</summary>
    private static async Task SetBorderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.BorderPoints =
        [
            new ShapePoint { Dx = 0.1, Dy = 0.1 },
            new ShapePoint { Dx = 0.8, Dy = 0.1 },
            new ShapePoint { Dx = 0.8, Dy = 0.8 },
            new ShapePoint { Dx = 0.1, Dy = 0.8 },
        ];
        await db.SaveChangesAsync();
    }

    private static async Task SeedGenerationLinkAsync(
        WallTestHarness h, Guid? oldHoldId, Guid? newHoldId, int fromGeneration, int toGeneration)
    {
        await using var db = h.CreateContext();

        db.HoldGenerationLinks.Add(new HoldGenerationLink
        {
            WallId = h.WallId,
            OldHoldId = oldHoldId,
            NewHoldId = newHoldId,
            Kind = HoldGenerationLinkKind.Same,
            FromGeneration = fromGeneration,
            ToGeneration = toGeneration,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AttachBoulderAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();

        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Live Route",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
