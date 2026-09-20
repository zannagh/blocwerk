using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the duplicate-virtual "join" merge. Virtual holds were invisible in the browser and the
/// boulder editor for a long time, so the same physical hold was added again and again; this merge
/// absorbs one placeholder into another. It is a DEDUPE, not a promotion: the survivor keeps its own
/// geometry and stays virtual, no boulder may be lost and none may go historic, and a boulder that
/// used BOTH duplicates must keep the stronger of the two hold roles rather than silently dropping one.
/// </summary>
public class MergeDuplicateVirtualHoldsTests
{
    [Fact]
    public async Task Merge_KeepsTheSurvivorsGeometryAndLeavesItVirtual()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, color: "red");
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.8, y: 0.8, color: "blue");

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        var survivor = await LoadHoldAsync(h, survivorId);

        Assert.Equal(0.2, survivor.X);
        Assert.Equal(0.2, survivor.Y);
        Assert.Equal("red", survivor.Color);
        Assert.True(survivor.IsVirtual);
    }

    [Fact]
    public async Task Merge_MovesABoulderThatOnlyKnewTheDuplicate()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);
        var boulderId = await AttachBoulderAsync(h, duplicateId, HoldType.Top, HoldUsage.HandOnly);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();
        var links = await db.BoulderHolds.AsNoTracking().Where(bh => bh.BoulderId == boulderId).ToListAsync();

        var link = Assert.Single(links);
        Assert.Equal(survivorId, link.HoldId);
        Assert.Equal(HoldType.Top, link.Type);
        Assert.Equal(HoldUsage.HandOnly, link.Usage);
    }

    [Fact]
    public async Task Merge_CollapsesABoulderThatKnewBothAndKeepsTheSpecificRole()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);

        // The owner's real-world case: the TOP flag sits on the duplicate, the survivor is a plain hold.
        var boulderId = await AttachBoulderAsync(h, survivorId, HoldType.Normal, HoldUsage.HandOnly);
        await AttachToBoulderAsync(h, boulderId, duplicateId, HoldType.Top, HoldUsage.FootOnly);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();
        var links = await db.BoulderHolds.AsNoTracking().Where(bh => bh.BoulderId == boulderId).ToListAsync();

        var link = Assert.Single(links);
        Assert.Equal(survivorId, link.HoldId);

        // Specific role beats the default Normal, so the Top flag survives the dedupe.
        Assert.Equal(HoldType.Top, link.Type);

        // HandOnly against FootOnly is a conflict between two deliberate values, so the ONE repo rule
        // (BoulderHoldReconciler: hand-capable beats foot-only) decides it. Widening to HandAndFoot
        // would invent a foothold neither setter gave.
        Assert.Equal(HoldUsage.HandOnly, link.Usage);
    }

    [Fact]
    public async Task Merge_KeepsTheStrongerRoleWhenBothAreSpecific()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);
        var boulderId = await AttachBoulderAsync(h, survivorId, HoldType.Start, HoldUsage.HandAndFoot);
        await AttachToBoulderAsync(h, boulderId, duplicateId, HoldType.Top, HoldUsage.HandAndFoot);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();
        var link = await db.BoulderHolds.AsNoTracking().SingleAsync(bh => bh.BoulderId == boulderId);

        // Top > Start > Normal, the repo's shipped prominence rule (BoulderHoldReconciler, mirroring
        // BoulderDetail's twin expansion). Keeping the survivor's Start here would leave the boulder
        // with NO top hold — a broken route — because the only Top sat on the absorbed duplicate.
        Assert.Equal(HoldType.Top, link.Type);
    }

    [Fact]
    public async Task Merge_KeepsADeliberateUsageAgainstTheDuplicatesDefault()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);

        // The common prod shape: the survivor was deliberately narrowed, the duplicate was added later
        // and simply kept the BoulderHold default (HandAndFoot).
        var boulderId = await AttachBoulderAsync(h, survivorId, HoldType.Normal, HoldUsage.HandOnly);
        await AttachToBoulderAsync(h, boulderId, duplicateId, HoldType.Normal, HoldUsage.HandAndFoot);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();
        var link = await db.BoulderHolds.AsNoTracking().SingleAsync(bh => bh.BoulderId == boulderId);

        // A default is not an intention: widening to HandAndFoot would grant a foothold the setter
        // never did (BoulderHold.Usage != HandAndFoot drives Boulder.FootholdMode.DefinedOnly).
        Assert.Equal(HoldUsage.HandOnly, link.Usage);
    }

    [Fact]
    public async Task Merge_NeverMarksABoulderHistoric()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);

        // The boulder that ONLY knows the duplicate is the one at risk: the duplicate row is deleted
        // under it. Deleting that same hold outright is asserted below to freeze it, so this test
        // fails the moment the merge degrades into a delete.
        var orphanedId = await AttachBoulderAsync(h, duplicateId, HoldType.Start, HoldUsage.HandAndFoot);
        await AttachBoulderAsync(h, survivorId, HoldType.Normal, HoldUsage.HandAndFoot);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using (var db = h.CreateContext())
        {
            var boulders = await db.Boulders.AsNoTracking().Where(b => b.WallId == h.WallId).ToListAsync();

            Assert.Equal(2, boulders.Count);
            Assert.All(boulders, b => Assert.False(b.IsHistoric));

            // The at-risk boulder did not merely survive: it kept a live hold to render from.
            Assert.True(await db.BoulderHolds.AsNoTracking()
                .AnyAsync(bh => bh.BoulderId == orphanedId && bh.HoldId == survivorId));
        }

        // Control: the delete path on the very same shape DOES freeze the boulder, so the assertions
        // above are discriminating rather than vacuous.
        await h.WallService.DeleteHoldAsync(survivorId);

        await using (var db = h.CreateContext())
        {
            Assert.True(await db.Boulders.AsNoTracking().AnyAsync(b => b.Id == orphanedId && b.IsHistoric));
        }
    }

    [Fact]
    public async Task Merge_RepointsTheDuplicatesPanelTwinLinkOntoTheSurvivor()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);
        var twinId = await SeedVirtualHoldAsync(h, x: 0.6, y: 0.6);
        await SeedHoldLinkAsync(h, duplicateId, twinId);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();

        Assert.False(await db.Holds.AsNoTracking().AnyAsync(x => x.Id == duplicateId));
        Assert.True(await db.Holds.AsNoTracking().AnyAsync(x => x.Id == survivorId));

        // A HoldLink is the curated "same physical hold on the adjacent panel" fact, not an alignment
        // artifact: dropping it would silently unlink the twin and stop BoulderHoldReconciler
        // expanding membership to it, so it moves onto the survivor instead.
        var link = Assert.Single(await db.HoldLinks.AsNoTracking().ToListAsync());
        Assert.Equal(survivorId, link.HoldAId);
        Assert.Equal(twinId, link.HoldBId);
    }

    [Fact]
    public async Task Merge_DropsAPanelLinkThatWouldSelfLinkOrCollide()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);
        var twinId = await SeedVirtualHoldAsync(h, x: 0.6, y: 0.6);

        // Both merged holds already link to the same twin (unordered, as the unique index demands),
        // and the two are linked to each other.
        await SeedHoldLinkAsync(h, survivorId, twinId);
        await SeedHoldLinkAsync(h, twinId, duplicateId);
        await SeedHoldLinkAsync(h, survivorId, duplicateId);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();

        var link = Assert.Single(await db.HoldLinks.AsNoTracking().ToListAsync());
        Assert.Equal(survivorId, link.HoldAId);
        Assert.Equal(twinId, link.HoldBId);
    }

    [Fact]
    public async Task Merge_RepointsGenerationLineageOnBothEnds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 1);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, generation: 1);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21, generation: 1);
        var ancestorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, generation: 0);
        var descendantId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, generation: 2);

        // Virtual holds ARE carried across generations, so the duplicate can sit on BOTH ends of the
        // lineage table. Both FKs are Restrict: before the fix this delete failed outright.
        await SeedGenerationLinkAsync(h, ancestorId, duplicateId, fromGeneration: 0, toGeneration: 1);
        await SeedGenerationLinkAsync(h, duplicateId, descendantId, fromGeneration: 1, toGeneration: 2);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();

        Assert.False(await db.Holds.AsNoTracking().AnyAsync(x => x.Id == duplicateId));

        // Lineage is preserved rather than erased: the survivor inherits both ends, and because both
        // holds stood on the same generation the stored From/To generations stay truthful.
        var links = await db.HoldGenerationLinks.AsNoTracking().ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.OldHoldId == ancestorId && l.NewHoldId == survivorId
            && l.FromGeneration == 0 && l.ToGeneration == 1);
        Assert.Contains(links, l => l.OldHoldId == survivorId && l.NewHoldId == descendantId
            && l.FromGeneration == 1 && l.ToGeneration == 2);
    }

    [Fact]
    public async Task Merge_DropsAGenerationLinkThatWouldCollideWithTheSurvivors()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 1);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, generation: 1);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21, generation: 1);
        var ancestorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, generation: 0);

        // Both placeholders were carried forward from the same predecessor: re-pointing the duplicate's
        // row would collide with the survivor's on the unique (OldHoldId, NewHoldId) index.
        await SeedGenerationLinkAsync(h, ancestorId, survivorId, fromGeneration: 0, toGeneration: 1);
        await SeedGenerationLinkAsync(h, ancestorId, duplicateId, fromGeneration: 0, toGeneration: 1);

        await h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId);

        await using var db = h.CreateContext();

        var link = Assert.Single(await db.HoldGenerationLinks.AsNoTracking().ToListAsync());
        Assert.Equal(ancestorId, link.OldHoldId);
        Assert.Equal(survivorId, link.NewHoldId);
    }

    [Fact]
    public async Task Merge_RejectsANonVirtualHold()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var actualId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21, isVirtual: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, actualId));

        // The reverse direction is refused too, and the non-virtual hold is untouched.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.MergeDuplicateVirtualHoldsAsync(actualId, survivorId));

        await using var db = h.CreateContext();
        Assert.Equal(2, await db.Holds.AsNoTracking().CountAsync(x => x.WallId == h.WallId));
    }

    [Fact]
    public async Task Merge_RejectsSelfMerge()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, survivorId));
    }

    [Fact]
    public async Task Merge_RejectsAHoldOnAnotherWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var foreignWallId = await SeedSecondWallAsync(h);
        var foreignId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, wallId: foreignWallId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, foreignId));

        await using var db = h.CreateContext();
        Assert.True(await db.Holds.AsNoTracking().AnyAsync(x => x.Id == foreignId));
    }

    [Fact]
    public async Task Merge_RejectsACallerWhoMayNotEditTheWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2);
        var duplicateId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21);

        // A plain member may climb the wall but not edit its holds.
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, duplicateId));

        await using var db = h.CreateContext();
        Assert.Equal(2, await db.Holds.AsNoTracking().CountAsync(x => x.WallId == h.WallId));
    }

    [Fact]
    public async Task Merge_RejectsHoldsOnDifferentGenerations()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: 1);
        var survivorId = await SeedVirtualHoldAsync(h, x: 0.2, y: 0.2, generation: 1);
        var oldGenerationId = await SeedVirtualHoldAsync(h, x: 0.21, y: 0.21, generation: 0);

        // Generations are immutable: absorbing across one would drag a boulder through time.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.WallService.MergeDuplicateVirtualHoldsAsync(survivorId, oldGenerationId));

        await using var db = h.CreateContext();
        Assert.Equal(2, await db.Holds.AsNoTracking().CountAsync(x => x.WallId == h.WallId));
    }

    private static async Task<Guid> SeedVirtualHoldAsync(
        WallTestHarness h,
        double x,
        double y,
        string? color = null,
        bool isVirtual = true,
        int generation = 0,
        Guid? wallId = null)
    {
        await using var db = h.CreateContext();

        var hold = new Hold
        {
            WallId = wallId ?? h.WallId,
            X = x,
            Y = y,
            Radius = 0.02,
            Color = color,
            IsVirtual = isVirtual,
            Generation = generation,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static async Task<Guid> SeedSecondWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var wall = new Wall { Name = "Other Wall", OwnerId = h.Owner.Id };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static async Task SeedGenerationLinkAsync(
        WallTestHarness h, Guid oldHoldId, Guid newHoldId, int fromGeneration, int toGeneration)
    {
        await using var db = h.CreateContext();

        db.HoldGenerationLinks.Add(new HoldGenerationLink
        {
            WallId = h.WallId,
            OldHoldId = oldHoldId,
            NewHoldId = newHoldId,
            FromGeneration = fromGeneration,
            ToGeneration = toGeneration,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedHoldLinkAsync(WallTestHarness h, Guid holdAId, Guid holdBId)
    {
        await using var db = h.CreateContext();

        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = holdAId, HoldBId = holdBId });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AttachBoulderAsync(WallTestHarness h, Guid holdId, HoldType type, HoldUsage usage)
    {
        await using var db = h.CreateContext();

        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "B",
            CreatedByUserId = h.Owner.Id,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId, Type = type, Usage = usage });
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task AttachToBoulderAsync(
        WallTestHarness h, Guid boulderId, Guid holdId, HoldType type, HoldUsage usage)
    {
        await using var db = h.CreateContext();

        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulderId, HoldId = holdId, Type = type, Usage = usage });
        await db.SaveChangesAsync();
    }

    private static async Task<Hold> LoadHoldAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().FirstAsync(x => x.Id == holdId);
    }
}
