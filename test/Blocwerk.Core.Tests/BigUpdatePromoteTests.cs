using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the boulder-survival core of the whole-wall big update under immutable generations
/// (design-phase3 Option B): promote RETAINS old gen-N hold rows as history, creates DISTINCT gen-N+1
/// rows, ties them with a <see cref="HoldGenerationLink"/>, and re-points advancing boulders so no
/// committed boulder ever loses a hold. Every test drives the real <see cref="WallBigUpdateService"/>
/// against the SQLite harness.
/// </summary>
public class BigUpdatePromoteTests
{
    [Fact]
    public async Task SameCarry_BoulderSurvives_PointsAtNewRow_OldRowRetainedAtGenN()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Carried, stagedId)));

        await using var db = h.CreateContext();
        // Old gen-0 row is retained as history.
        var oldRow = await db.Holds.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(0, oldRow.Generation);
        // The staged twin IS the new-generation live row.
        var newRow = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.Equal(1, newRow.Generation);
        // The boulder's membership now points at the new row, and the boulder advanced.
        var bh = await db.BoulderHolds.SingleAsync(x => x.BoulderId == boulderId);
        Assert.Equal(stagedId, bh.HoldId);
        var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        Assert.Equal(1, boulder.Generation);
        Assert.False(boulder.NeedsReview);
        Assert.False(boulder.IsHistoric);
    }

    [Fact]
    public async Task SameCarry_CuratedFieldsCopiedOntoNewRow_StagedPositionKept()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetCurationAsync(h, old.Id, name: "Sloper A", color: "blue", category: HoldCategory.Foot);
        var (_, stagedId) = await SeedStagedCenterAsync(h, stagedX: 0.6, stagedY: 0.7, stagedRadius: 0.05);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Carried, stagedId)));

        await using var db = h.CreateContext();
        var newRow = await db.Holds.SingleAsync(x => x.Id == stagedId);
        // Curation copied old -> staged.
        Assert.Equal("Sloper A", newRow.Name);
        Assert.Equal("blue", newRow.Color);
        Assert.Equal(HoldCategory.Foot, newRow.Category);
        // Staged (user-corrected) position kept, not the old hold's.
        Assert.Equal(0.6, newRow.X, 3);
        Assert.Equal(0.7, newRow.Y, 3);
        Assert.Equal(0.05, newRow.Radius, 3);
    }

    [Fact]
    public async Task ChangedCarry_FlagsBoulderAndHoldNeedsReview_LinkKindChanged()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Changed, stagedId)));

        await using var db = h.CreateContext();
        var newRow = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.True(newRow.NeedsReview);
        var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        Assert.True(boulder.NeedsReview);
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.NewHoldId == stagedId);
        Assert.Equal(HoldGenerationLinkKind.Changed, link.Kind);
        Assert.Equal(old.Id, link.OldHoldId);
        Assert.Equal(0, link.FromGeneration);
        Assert.Equal(1, link.ToGeneration);
    }

    [Fact]
    public async Task Removed_BoulderHistoric_OldRowNotDeleted_NoLink()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Removed, null)));

        await using var db = h.CreateContext();
        // Old row is RETAINED (history), still at gen 0.
        var oldRow = await db.Holds.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(0, oldRow.Generation);
        // The boulder became historic but KEEPS its membership on the retained gen-0 row; no lineage link.
        var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        Assert.True(boulder.IsHistoric);
        Assert.True(await db.BoulderHolds.AnyAsync(x => x.BoulderId == boulderId && x.HoldId == old.Id));
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == old.Id));
    }

    [Fact]
    public async Task UnmappedCarry_ClonesOldPosition_RePointsBoulder_LinksSame()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        // A staged detection exists but is NOT the twin — the old hold is carried "blind".
        await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Carried, null)));

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == old.Id);
        Assert.Equal(HoldGenerationLinkKind.Same, link.Kind);
        Assert.NotEqual(old.Id, link.NewHoldId);
        // The clone sits at the old position, at the new generation.
        var clone = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.Equal(1, clone.Generation);
        Assert.Equal(old.X, clone.X, 3);
        Assert.Equal(old.Y, clone.Y, 3);
        // Old row retained; boulder re-pointed at the clone and advanced.
        Assert.Equal(0, (await db.Holds.SingleAsync(x => x.Id == old.Id)).Generation);
        var bh = await db.BoulderHolds.SingleAsync(x => x.BoulderId == boulderId);
        Assert.Equal(link.NewHoldId, bh.HoldId);
        Assert.Equal(1, (await db.Boulders.SingleAsync(b => b.Id == boulderId)).Generation);
    }

    // Warp-carry: an old hold with NO staged twin but WITH a matcher warp position carries forward to
    // the WARPED coordinates (not its stale old position), so its boulder lands on the real hold and
    // needs no revision. Boulder membership survives on the successor; the old gen-0 row is retained.
    [Fact]
    public async Task UnmatchedCarry_WithWarpPosition_ClonesAtWarpedCoords_BoulderSurvives()
    {
        using var h = new WallTestHarness();
        // Seeded at (0.1, 0.1) with radius 0.02; a staged detection exists but is NOT the twin, so the
        // old hold is carried "blind" (no staged position to snap to) and must land on the warp position.
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        // The matcher warp-predicted this old hold's new-image position; promote must clone there.
        const double warpedX = 0.82;
        const double warpedY = 0.31;
        var confirmation = new BigUpdateConfirmation(
            [Carry(old.Id, CarryKind.Carried, null)],
            [],
            [],
            [],
            new Dictionary<Guid, HoldPositionNorm> { [old.Id] = new HoldPositionNorm(warpedX, warpedY) });

        await service.PromoteAsync(h.WallId, confirmation);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == old.Id);
        var clone = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        // Clone sits at the WARPED position, at the new generation; radius/curation unchanged.
        Assert.Equal(1, clone.Generation);
        Assert.Equal(warpedX, clone.X, 3);
        Assert.Equal(warpedY, clone.Y, 3);
        Assert.NotEqual(old.X, clone.X, 3);
        Assert.Equal(old.Radius, clone.Radius, 3);
        // Old gen-0 row retained; boulder re-pointed onto the successor and advanced.
        Assert.Equal(0, (await db.Holds.SingleAsync(x => x.Id == old.Id)).Generation);
        var bh = await db.BoulderHolds.SingleAsync(x => x.BoulderId == boulderId);
        Assert.Equal(clone.Id, bh.HoldId);
        Assert.Equal(1, (await db.Boulders.SingleAsync(b => b.Id == boulderId)).Generation);
    }

    // Warp-carry (shapes): an UNMATCHED old hold with a custom outline carries forward with its polygon
    // WARPED onto the new image — the successor's ShapePoints become the warped vertices (rebased on the
    // successor's warped centre), not the stale old outline. Boulder survives; the old gen-0 row is kept.
    [Fact]
    public async Task UnmatchedCarry_WithWarpShape_SetsSuccessorShapeToWarpedPolygon_BoulderSurvives()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        // A custom triangular outline on the old hold (centre-relative offsets).
        await SetShapeAsync(h, old.Id, [(-0.03, -0.02), (0.03, -0.02), (0.0, 0.04)]);
        await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        const double warpedX = 0.82;
        const double warpedY = 0.31;
        // The matcher's warp-predicted new-image polygon (ABSOLUTE new-image normalized vertices).
        List<HoldPositionNorm> warpedPolygon = [new(0.78, 0.28), new(0.86, 0.29), new(0.83, 0.36)];
        var confirmation = new BigUpdateConfirmation(
            [Carry(old.Id, CarryKind.Carried, null)],
            [],
            [],
            [],
            new Dictionary<Guid, HoldPositionNorm> { [old.Id] = new HoldPositionNorm(warpedX, warpedY) },
            new Dictionary<Guid, IReadOnlyList<HoldPositionNorm>> { [old.Id] = warpedPolygon });

        await service.PromoteAsync(h.WallId, confirmation);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == old.Id);
        var clone = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.Equal(warpedX, clone.X, 3);
        Assert.Equal(warpedY, clone.Y, 3);
        // ShapePoints are the WARPED polygon, stored as offsets against the successor's warped centre.
        Assert.NotNull(clone.ShapePoints);
        Assert.Equal(warpedPolygon.Count, clone.ShapePoints!.Count);
        for (var i = 0; i < warpedPolygon.Count; i++)
        {
            Assert.Equal(warpedPolygon[i].X, clone.X + clone.ShapePoints[i].Dx, 5);
            Assert.Equal(warpedPolygon[i].Y, clone.Y + clone.ShapePoints[i].Dy, 5);
        }

        // The old gen-0 row keeps its OWN (un-warped) outline; boulder advanced onto the successor.
        var oldRow = await db.Holds.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(0, oldRow.Generation);
        Assert.Equal(-0.03, oldRow.ShapePoints![0].Dx, 5);
        Assert.Equal(clone.Id, (await db.BoulderHolds.SingleAsync(x => x.BoulderId == boulderId)).HoldId);
        Assert.Equal(1, (await db.Boulders.SingleAsync(b => b.Id == boulderId)).Generation);
    }

    // Warp-carry (shapes): a MATCHED old hold with a custom outline must keep its WARPED custom outline on
    // the successor twin — a detection twin has no hand-drawn shape, so without this it would revert to a
    // plain circle. The warped polygon is rebased on the twin's OWN detected centre.
    [Fact]
    public async Task MatchedCarry_WithWarpShape_TwinGetsWarpedPolygon_NotPlainCircle()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetShapeAsync(h, old.Id, [(-0.03, -0.02), (0.03, -0.02), (0.0, 0.04)]);
        var (_, stagedId) = await SeedStagedCenterAsync(h); // staged twin at (0.5, 0.5), no ShapePoints
        var boulderId = await AttachBoulderAsync(h, old.Id);
        var service = NewService(h);

        List<HoldPositionNorm> warpedPolygon = [new(0.47, 0.48), new(0.53, 0.48), new(0.50, 0.54)];
        var confirmation = new BigUpdateConfirmation(
            [Carry(old.Id, CarryKind.Carried, stagedId)],
            [],
            [],
            [],
            null,
            new Dictionary<Guid, IReadOnlyList<HoldPositionNorm>> { [old.Id] = warpedPolygon });

        await service.PromoteAsync(h.WallId, confirmation);

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.Equal(1, twin.Generation);
        // The twin (a plain detection) now carries the WARPED custom outline, rebased on its own centre.
        Assert.NotNull(twin.ShapePoints);
        Assert.Equal(warpedPolygon.Count, twin.ShapePoints!.Count);
        for (var i = 0; i < warpedPolygon.Count; i++)
        {
            Assert.Equal(warpedPolygon[i].X, twin.X + twin.ShapePoints[i].Dx, 5);
            Assert.Equal(warpedPolygon[i].Y, twin.Y + twin.ShapePoints[i].Dy, 5);
        }

        Assert.Equal(stagedId, (await db.BoulderHolds.SingleAsync(x => x.BoulderId == boulderId)).HoldId);
    }

    [Fact]
    public async Task Promote_LeavesBothGenerations_QueryableByGeneration()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Carried, stagedId)));

        var genOld = await h.WallService.GetHoldsForGenerationAsync(h.WallId, 0);
        var genNew = await h.WallService.GetHoldsForGenerationAsync(h.WallId, 1);
        Assert.Contains(genOld, x => x.Id == old.Id);
        Assert.Contains(genNew, x => x.Id == stagedId);
        Assert.DoesNotContain(genNew, x => x.Id == old.Id);
        Assert.DoesNotContain(genOld, x => x.Id == stagedId);
    }

    [Fact]
    public async Task Promote_HistoricBoulderKeepsOldRow_WhileActiveBoulderAdvances()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var activeId = await AttachBoulderAsync(h, old.Id);
        var historicId = await AttachBoulderAsync(h, old.Id, historic: true);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(old.Id, CarryKind.Carried, stagedId)));

        await using var db = h.CreateContext();
        // Active boulder advanced onto the new row.
        Assert.Equal(stagedId, (await db.BoulderHolds.SingleAsync(x => x.BoulderId == activeId)).HoldId);
        // Historic boulder still points at the retained old gen-0 row.
        Assert.Equal(old.Id, (await db.BoulderHolds.SingleAsync(x => x.BoulderId == historicId)).HoldId);
        Assert.Equal(0, (await db.Boulders.SingleAsync(b => b.Id == historicId)).Generation);
    }

    [Fact]
    public async Task NeighbourLinks_ResolveToLiveCentreHold()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var (neighbourPanelId, neighbourHoldId) = await SeedNeighbourPanelAsync(h);
        var service = NewService(h);

        var confirmation = new BigUpdateConfirmation(
            [Carry(old.Id, CarryKind.Carried, stagedId)],
            [],
            [],
            [new NeighbourLinkSet(neighbourPanelId, [new ConfirmedLink(stagedId, neighbourHoldId, false)], [])]);

        await service.PromoteAsync(h.WallId, confirmation);

        await using var db = h.CreateContext();
        var link = await db.HoldLinks.SingleAsync();
        Assert.Equal(stagedId, link.HoldAId);
        Assert.Equal(neighbourHoldId, link.HoldBId);
        Assert.Equal(HoldLinkKind.Same, link.Kind);
        Assert.Equal(1, (await db.Holds.SingleAsync(x => x.Id == neighbourHoldId)).Generation);
    }

    // F1 (data loss): a Removed hold's row is RETAINED, so the frozen boulder must KEEP its (B, R)
    // membership — nothing gets deleted in the removed path — and its schematic still shows the hold.
    [Fact]
    public async Task Removed_FrozenBoulderKeepsMembership_AndRowRetained()
    {
        using var h = new WallTestHarness();
        var removed = (await h.SeedWallAsync(holdCount: 1))[0];
        await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderAsync(h, removed.Id);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(removed.Id, CarryKind.Removed, null)));

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == boulderId)).IsHistoric);
        // The (B, R) membership survives on the retained gen-0 row.
        Assert.True(await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == boulderId && bh.HoldId == removed.Id));
        Assert.Equal(0, (await db.Holds.SingleAsync(x => x.Id == removed.Id)).Generation);
    }

    // F2 (order-dependent corruption): a boulder sharing a Removed hold R and a Carried hold C must end
    // fully frozen at gen N regardless of the decision order. Runs both orders and asserts an identical
    // result — B historic, still at oldGen, both memberships still on the retained gen-N rows.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Removed_And_Carried_ShareBoulder_OrderIndependent(bool removedFirst)
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var removed = holds[0];
        var carried = holds[1];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderWithHoldsAsync(h, removed.Id, carried.Id);
        var service = NewService(h);

        var removedDecision = Carry(removed.Id, CarryKind.Removed, null);
        var carriedDecision = Carry(carried.Id, CarryKind.Carried, stagedId);
        var confirmation = removedFirst
            ? Confirm(removedDecision, carriedDecision)
            : Confirm(carriedDecision, removedDecision);

        await service.PromoteAsync(h.WallId, confirmation);

        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        Assert.True(boulder.IsHistoric);
        Assert.Equal(0, boulder.Generation);
        // Both memberships stayed on the retained gen-0 rows — no bumped gen-1 pointer.
        var memberships = await db.BoulderHolds.Where(bh => bh.BoulderId == boulderId).Select(bh => bh.HoldId).ToListAsync();
        Assert.Equal(2, memberships.Count);
        Assert.Contains(removed.Id, memberships);
        Assert.Contains(carried.Id, memberships);
    }

    // F3 (crash + curation loss): two old holds merging onto ONE staged twin, with one boulder holding
    // both, must not throw a duplicate-key on the (B, N) insert. B ends with a single membership to the
    // merged hold; both lineage links exist; curation is first-writer-wins (not silently last-writer).
    [Fact]
    public async Task PhysicalMerge_TwoOldHoldsOneTwin_Idempotent_NoDuplicateMembership()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var o1 = holds[0];
        var o2 = holds[1];
        await SetCurationAsync(h, o1.Id, name: "First", color: "red", category: HoldCategory.Hand);
        await SetCurationAsync(h, o2.Id, name: "Second", color: "green", category: HoldCategory.Foot);
        var (_, twinId) = await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderWithHoldsAsync(h, o1.Id, o2.Id);
        var service = NewService(h);

        await service.PromoteAsync(
            h.WallId, Confirm(Carry(o1.Id, CarryKind.Carried, twinId), Carry(o2.Id, CarryKind.Carried, twinId)));

        await using var db = h.CreateContext();
        // Exactly one membership, pointing at the merged twin.
        var memberships = await db.BoulderHolds.Where(bh => bh.BoulderId == boulderId).ToListAsync();
        Assert.Single(memberships);
        Assert.Equal(twinId, memberships[0].HoldId);
        // Both old holds recorded their lineage onto the shared twin.
        Assert.Equal(2, await db.HoldGenerationLinks.CountAsync(l => l.NewHoldId == twinId));
        // First-writer-wins curation: O1's, not O2's.
        Assert.Equal("First", (await db.Holds.SingleAsync(x => x.Id == twinId)).Name);
    }

    // F4 (orphan): an old hold with NO decision in the outcome must still be carried forward, so a
    // boulder advanced by its other holds does not silently drop it. Here the boulder holds an
    // undeclared hold U plus a carried hold C; after promote it references U's gen-1 successor.
    [Fact]
    public async Task UndeclaredOldHold_IsDefaultCarried_BoulderKeepsIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var undeclared = holds[0];
        var carried = holds[1];
        var (_, stagedId) = await SeedStagedCenterAsync(h);
        var boulderId = await AttachBoulderWithHoldsAsync(h, undeclared.Id, carried.Id);
        var service = NewService(h);

        // Only the carried hold is declared; the undeclared hold has no decision at all.
        await service.PromoteAsync(h.WallId, Confirm(Carry(carried.Id, CarryKind.Carried, stagedId)));

        await using var db = h.CreateContext();
        // The undeclared hold was cloned forward and linked Same.
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == undeclared.Id);
        Assert.Equal(HoldGenerationLinkKind.Same, link.Kind);
        var successor = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.Equal(1, successor.Generation);
        // The advancing boulder points at the successor, not the stale gen-0 row.
        var memberships = await db.BoulderHolds.Where(bh => bh.BoulderId == boulderId).Select(bh => bh.HoldId).ToListAsync();
        Assert.Contains(successor.Id, memberships);
        Assert.DoesNotContain(undeclared.Id, memberships);
        Assert.Equal(1, (await db.Boulders.SingleAsync(b => b.Id == boulderId)).Generation);
    }

    private static WallBigUpdateService NewService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

    private static CarryoverDecision Carry(Guid oldId, CarryKind kind, Guid? newId) =>
        new(oldId, kind, newId);

    private static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    // Stages the in-flight big update's centre panel at generation 1 with one staged detection.
    private static async Task<(Guid CenterPanelId, Guid StagedHoldId)> SeedStagedCenterAsync(
        WallTestHarness h, double stagedX = 0.5, double stagedY = 0.5, double stagedRadius = 0.02)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = 0,
            Row = 0,
            Photo = null,
            StagedPhoto = [1, 2, 3],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(panel);

        var staged = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = stagedX,
            Y = stagedY,
            Radius = stagedRadius,
            Generation = 1,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(staged);
        await db.SaveChangesAsync();
        return (panel.Id, staged.Id);
    }

    private static async Task<(Guid PanelId, Guid HoldId)> SeedNeighbourPanelAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = 1,
            Row = 0,
            Photo = null,
            StagedPhoto = [4, 5, 6],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(panel);

        var hold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = 0.2,
            Y = 0.2,
            Radius = 0.02,
            Generation = 1,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return (panel.Id, hold.Id);
    }

    // Adds a boulder that uses the given hold, returning its id. Historic boulders must keep pointing
    // at the retained old row after promote; active ones must advance.
    private static async Task<Guid> AttachBoulderAsync(WallTestHarness h, Guid holdId, bool historic = false)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = historic ? "Historic" : "Active",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
            IsHistoric = historic,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    // Adds a single active boulder that uses several holds, returning its id.
    private static async Task<Guid> AttachBoulderWithHoldsAsync(WallTestHarness h, params Guid[] holdIds)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Multi",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        foreach (var holdId in holdIds)
        {
            db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        }

        await db.SaveChangesAsync();
        return boulder.Id;
    }

    // Sets a custom outline on a hold from centre-relative (Dx, Dy) offsets.
    private static async Task SetShapeAsync(WallTestHarness h, Guid holdId, (double Dx, double Dy)[] points)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.ShapePoints = points.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        await db.SaveChangesAsync();
    }

    private static async Task SetCurationAsync(
        WallTestHarness h, Guid holdId, string name, string color, HoldCategory category)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.Name = name;
        hold.Color = color;
        hold.Category = category;
        await db.SaveChangesAsync();
    }
}
