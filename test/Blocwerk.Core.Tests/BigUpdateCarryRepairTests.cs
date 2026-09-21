using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for two promote defects that only ever showed up on a wall that had actually been updated:
/// a warp-predicted outline written onto a hold it does not belong to (a hold that physically MOVED
/// rendered its body at the OLD spot and slid off the viewBox on the first drag), and the wall's whole
/// cross-panel <see cref="HoldLink"/> set being dropped at every generation bump.
/// </summary>
public class BigUpdateCarryRepairTests
{
    // D1. The old hold MOVED: its staged twin sits far from it, so the matcher's warp predicts the OLD
    // location. That polygon is not this twin's outline and must be refused — the twin keeps its OWN
    // detected outline rather than a body drawn a third of the wall away from its centre.
    [Fact]
    public async Task MatchedCarry_MovedHold_TwinKeepsOwnDetectedOutline()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0]; // (0.1, 0.1)
        await SetShapeAsync(h, old.Id, [(-0.03, -0.02), (0.03, -0.02), (0.0, 0.04)]);
        var (_, stagedId) = await SeedStagedCenterAsync(h, stagedX: 0.5, stagedY: 0.5);
        // The twin carries its OWN detected outline, which is the truth for the hold's new position.
        await SetShapeAsync(h, stagedId, [(-0.02, -0.02), (0.02, -0.02), (0.0, 0.03)]);
        var service = NewService(h);

        // The warp predicts the old hold's pixels around its OLD centre (0.1, 0.1).
        List<HoldPositionNorm> warpedAtOldSpot = [new(0.07, 0.08), new(0.13, 0.08), new(0.10, 0.14)];
        await service.PromoteAsync(h.WallId, WithShapes(old.Id, stagedId, warpedAtOldSpot));

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.Equal(1, twin.Generation);
        Assert.NotNull(twin.ShapePoints);
        // Its own outline, untouched — and every vertex still hugs the twin's centre.
        Assert.Equal(-0.02, twin.ShapePoints![0].Dx, 5);
        Assert.All(twin.ShapePoints, p => Assert.True(Math.Abs(p.Dx) < 0.05 && Math.Abs(p.Dy) < 0.05));
    }

    // D1. Same refusal when the twin has NO outline of its own — a staged detection never gets one, so
    // this is the ordinary case. The hold comes out of the promote as a plain circle, which COSTS the
    // predecessor's traced outline, so the refusal must not be silent: the successor is flagged for
    // review instead of passing as a hold that simply never had a shape.
    [Fact]
    public async Task MatchedCarry_WarpShapeFarFromSuccessor_IsRefused_AndFlaggedForReview()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetShapeAsync(h, old.Id, [(-0.03, -0.02), (0.03, -0.02), (0.0, 0.04)]);
        var (_, stagedId) = await SeedStagedCenterAsync(h, stagedX: 0.5, stagedY: 0.5);
        var service = NewService(h);

        List<HoldPositionNorm> warpedAtOldSpot = [new(0.07, 0.08), new(0.13, 0.08), new(0.10, 0.14)];
        // Carried as unchanged, which is the path that CLEARS NeedsReview — so a set flag afterwards can
        // only have come from the refusal.
        await service.PromoteAsync(h.WallId, WithShapes(old.Id, stagedId, warpedAtOldSpot));

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.Null(twin.ShapePoints);
        Assert.True(twin.NeedsReview);
    }

    // F1/F2 discriminator. The refusal must key on the outline's OWN extent, not on the hold's radius.
    // The Shape tool drags vertices one at a time and never updates X/Y/Radius (changing the radius
    // RESETS the polygon to an octagon), so a rail or volume traced out of a small detection keeps that
    // small radius for ever and its vertex mean legitimately sits several radii off-centre. Under a
    // radius-only rule this healthy outline was "detached"; it must be carried through untouched.
    [Fact]
    public async Task MatchedCarry_LopsidedButCorrectOutline_IsNotRefused()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h, stagedX: 0.5, stagedY: 0.5);
        var service = NewService(h);

        // A 0.2-wide rail around a radius-0.02 hold, deliberately lopsided: its vertex mean sits 0.075
        // from the twin's centre — far outside 2 * 0.02 — while its own extent is ~0.11, so the hold's
        // centre is comfortably inside the polygon. This is what a hand-traced rail looks like.
        List<HoldPositionNorm> lopsidedRail =
        [
            new(0.45, 0.48), new(0.70, 0.48), new(0.70, 0.52), new(0.45, 0.52),
        ];
        await service.PromoteAsync(h.WallId, WithShapes(old.Id, stagedId, lopsidedRail));

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.NotNull(twin.ShapePoints);
        for (var i = 0; i < lopsidedRail.Count; i++)
        {
            Assert.Equal(lopsidedRail[i].X, twin.X + twin.ShapePoints![i].Dx, 5);
            Assert.Equal(lopsidedRail[i].Y, twin.Y + twin.ShapePoints[i].Dy, 5);
        }

        Assert.False(twin.NeedsReview);
    }

    // The other half of the same discriminator: the SAME rail, translated bodily off its hold — an
    // (old - new) offset, which is what a moved hold's warp produces. The hold's centre is then outside
    // the polygon entirely, and it is refused however lopsided-looking the shape is.
    [Fact]
    public async Task MatchedCarry_TranslatedOutline_IsRefused()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        var (_, stagedId) = await SeedStagedCenterAsync(h, stagedX: 0.5, stagedY: 0.5);
        var service = NewService(h);

        // The rail above, moved 0.3 to the left: same size and form, nowhere near the twin at 0.5.
        List<HoldPositionNorm> translatedRail =
        [
            new(0.15, 0.48), new(0.40, 0.48), new(0.40, 0.52), new(0.15, 0.52),
        ];
        await service.PromoteAsync(h.WallId, WithShapes(old.Id, stagedId, translatedRail));

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.Null(twin.ShapePoints);
        Assert.True(twin.NeedsReview);
    }

    // D1, the other side of the guard: a warp that really does land on the twin is still written, so the
    // fix costs nothing for a hold that stayed put (the whole point of warp-carrying a custom outline).
    [Fact]
    public async Task MatchedCarry_WarpShapeOnSuccessor_StillApplied()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetShapeAsync(h, old.Id, [(-0.03, -0.02), (0.03, -0.02), (0.0, 0.04)]);
        var (_, stagedId) = await SeedStagedCenterAsync(h, stagedX: 0.5, stagedY: 0.5);
        var service = NewService(h);

        List<HoldPositionNorm> warpedOnTwin = [new(0.47, 0.48), new(0.53, 0.48), new(0.50, 0.54)];
        await service.PromoteAsync(h.WallId, WithShapes(old.Id, stagedId, warpedOnTwin));

        await using var db = h.CreateContext();
        var twin = await db.Holds.SingleAsync(x => x.Id == stagedId);
        Assert.NotNull(twin.ShapePoints);
        for (var i = 0; i < warpedOnTwin.Count; i++)
        {
            Assert.Equal(warpedOnTwin[i].X, twin.X + twin.ShapePoints![i].Dx, 5);
            Assert.Equal(warpedOnTwin[i].Y, twin.Y + twin.ShapePoints[i].Dy, 5);
        }
    }

    // D2 regression. A cross-panel link between a centre hold and a neighbour hold must be re-stated
    // between their successors, not left pointing at the retained gen-0 rows (which wiped the wall's
    // whole link set on every update).
    [Fact]
    public async Task Promote_ExistingCrossPanelLink_IsRemappedOntoSuccessors()
    {
        using var h = new WallTestHarness();
        var oldCentre = (await h.SeedWallAsync(holdCount: 1))[0];
        var oldNeighbour = await SeedLiveNeighbourHoldAsync(h);
        await LinkAsync(h, oldCentre.Id, oldNeighbour.Id);
        var (_, stagedCentreId) = await SeedStagedCenterAsync(h);
        var (_, stagedNeighbourId) = await SeedStagedNeighbourAsync(h);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(
            Carry(oldCentre.Id, CarryKind.Carried, stagedCentreId),
            Carry(oldNeighbour.Id, CarryKind.Carried, stagedNeighbourId)));

        await using var db = h.CreateContext();
        var link = await db.HoldLinks.SingleAsync();
        Assert.Equal(
            new HashSet<Guid> { stagedCentreId, stagedNeighbourId },
            new HashSet<Guid> { link.HoldAId, link.HoldBId });
        // The superseded row is retired, not left behind to fuse gen-0 rows into the live component.
        Assert.False(await db.HoldLinks.AnyAsync(l => l.HoldAId == oldCentre.Id || l.HoldBId == oldCentre.Id));
    }

    // D2, subset promote. The neighbour panel was NOT re-shot, so its hold stays live at gen 0 with no
    // successor. The link must survive with that end exactly where it is — the same "each end resolves at
    // ITS panel's live generation" rule the boulder repoint follows.
    [Fact]
    public async Task Promote_LinkSpanningANonUpdatedPanel_KeepsTheUntouchedEnd()
    {
        using var h = new WallTestHarness();
        var oldCentre = (await h.SeedWallAsync(holdCount: 1))[0];
        var neighbour = await SeedLiveNeighbourHoldAsync(h);
        await LinkAsync(h, oldCentre.Id, neighbour.Id);
        var (_, stagedCentreId) = await SeedStagedCenterAsync(h);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(Carry(oldCentre.Id, CarryKind.Carried, stagedCentreId)));

        await using var db = h.CreateContext();
        var link = await db.HoldLinks.SingleAsync();
        Assert.Equal(
            new HashSet<Guid> { stagedCentreId, neighbour.Id },
            new HashSet<Guid> { link.HoldAId, link.HoldBId });
    }

    // D2, the removed end. A carried hold the user marked Removed has no successor, so the physical hold
    // is gone at the new generation and the link is dropped rather than re-pointed at a retired row.
    [Fact]
    public async Task Promote_LinkWhoseEndWasRemoved_IsDropped()
    {
        using var h = new WallTestHarness();
        var oldCentre = (await h.SeedWallAsync(holdCount: 1))[0];
        var oldNeighbour = await SeedLiveNeighbourHoldAsync(h);
        await LinkAsync(h, oldCentre.Id, oldNeighbour.Id);
        var (_, stagedCentreId) = await SeedStagedCenterAsync(h);
        var (_, stagedNeighbourId) = await SeedStagedNeighbourAsync(h);
        var service = NewService(h);

        await service.PromoteAsync(h.WallId, Confirm(
            Carry(oldCentre.Id, CarryKind.Removed, null),
            Carry(oldNeighbour.Id, CarryKind.Carried, stagedNeighbourId)));

        await using var db = h.CreateContext();
        Assert.False(await db.HoldLinks.AnyAsync());
        // The surviving end still went live — the link was dropped, not the hold.
        Assert.Equal(1, (await db.Holds.SingleAsync(x => x.Id == stagedNeighbourId)).Generation);
    }

    private static BigUpdateConfirmation WithShapes(
        Guid oldId, Guid twinId, IReadOnlyList<HoldPositionNorm> polygon) =>
        new(
            [Carry(oldId, CarryKind.Carried, twinId)],
            [],
            [],
            [],
            null,
            new Dictionary<Guid, IReadOnlyList<HoldPositionNorm>> { [oldId] = polygon });

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

    private static async Task<(Guid PanelId, Guid HoldId)> SeedStagedCenterAsync(
        WallTestHarness h, double stagedX = 0.5, double stagedY = 0.5) =>
        await SeedStagedPanelAsync(h, col: 0, row: 0, x: stagedX, y: stagedY);

    private static async Task<(Guid PanelId, Guid HoldId)> SeedStagedNeighbourAsync(WallTestHarness h) =>
        await SeedStagedPanelAsync(h, col: 1, row: 0, x: 0.3, y: 0.3);

    // A staged (in-flight) panel at generation 1 carrying one fresh detection.
    private static async Task<(Guid PanelId, Guid HoldId)> SeedStagedPanelAsync(
        WallTestHarness h, int col, int row, double x, double y)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = col,
            Row = row,
            StagedPhoto = [1, 2, 3],
            StagedPhotoContentType = "image/jpeg",
            Generation = 1,
        };
        db.WallPanels.Add(panel);

        var staged = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = x,
            Y = y,
            Radius = 0.02,
            Generation = 1,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(staged);
        await db.SaveChangesAsync();
        return (panel.Id, staged.Id);
    }

    // A LIVE generation-0 neighbour panel at (1,0) plus one hold on it — the far end of a cross-panel link.
    private static async Task<Hold> SeedLiveNeighbourHoldAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = 1,
            Row = 0,
            Photo = [7, 8, 9],
            PhotoContentType = "image/jpeg",
            Generation = 0,
        };
        db.WallPanels.Add(panel);

        var hold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panel.Id,
            X = 0.25,
            Y = 0.25,
            Radius = 0.02,
            Generation = 0,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold;
    }

    private static async Task LinkAsync(WallTestHarness h, Guid holdAId, Guid holdBId)
    {
        await using var db = h.CreateContext();
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = holdAId, HoldBId = holdBId });
        await db.SaveChangesAsync();
    }

    private static async Task SetShapeAsync(WallTestHarness h, Guid holdId, (double Dx, double Dy)[] points)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.ShapePoints = points.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        await db.SaveChangesAsync();
    }
}
