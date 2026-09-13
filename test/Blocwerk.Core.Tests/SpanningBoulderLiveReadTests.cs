using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the SPANNING-boulder live read after a subset (per-panel) promote — the correctness bug the
/// partial-repoint fix closes. A boulder whose holds straddle an UPDATED panel (the re-shot centre) AND a
/// NON-updated panel (the untouched right) must still render ALL its holds in the live view: the centre
/// hold resolved via its gen-(N+1) successor and the right hold left at gen-N. The old skip-entirely
/// behaviour left every membership on the retired gen-N centre row, which the live-panel read excludes,
/// so the boulder drew missing its centre hold. Drives the real <see cref="WallBigUpdateService"/> promote
/// against the SQLite harness (mirroring <see cref="BigUpdateSubsetPromoteTests"/>), then reads back
/// through <see cref="WallService.GetWallAsync"/> and asserts neither hold vanishes.
/// </summary>
public class SpanningBoulderLiveReadTests
{
    // The headline invariant: after a centre-only promote, the spanning boulder resolves BOTH holds in the
    // live read — the centre hold via its gen-3 successor, the right hold at gen-2 — and stays an active,
    // non-review boulder. NON-VACUOUS: the pre-fix skip left the centre membership on the retired gen-2
    // centre row (w.CentreHoldId), which is NOT in the live set, so the stagedHoldId assertion below fails.
    [Fact]
    public async Task AfterCentreOnlyPromote_SpanningBoulder_ResolvesBothHoldsLive_StaysActive()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallWithSpanningBoulderAsync(h);
        var (_, stagedHoldId) = await StageCentreUpdateAsync(h, w.WallId);

        // Only the centre hold is declared — the review layer never surfaces non-updated-panel holds.
        await NewBigUpdateService(h).PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, stagedHoldId)));

        var wall = await h.WallService.GetWallAsync(w.WallId);
        Assert.NotNull(wall);

        // The live hold set spans generations: the re-shot centre's gen-3 twin and the untouched right's
        // gen-2 hold. The superseded old centre row is excluded (its panel is retired).
        var liveHoldIds = wall!.Holds.Select(x => x.Id).ToHashSet();
        Assert.Contains(stagedHoldId, liveHoldIds);
        Assert.Contains(w.RightHoldId, liveHoldIds);
        Assert.DoesNotContain(w.CentreHoldId, liveHoldIds);

        // The spanning boulder now points at the gen-3 successor (centre) AND the gen-2 right hold — and
        // BOTH resolve in the live read, i.e. neither hold vanishes. This is exactly the set BoulderDetail
        // filters to the live panels, so a full render is guaranteed.
        var spanBoulder = wall.Boulders.Single(b => b.Id == w.SpanningBoulderId);
        var membershipHoldIds = spanBoulder.BoulderHolds.Select(bh => bh.HoldId).ToList();
        Assert.Equal(2, membershipHoldIds.Count);
        Assert.Contains(stagedHoldId, membershipHoldIds);   // centre hold advanced to its successor
        Assert.Contains(w.RightHoldId, membershipHoldIds);  // right hold left at gen-2
        Assert.DoesNotContain(w.CentreHoldId, membershipHoldIds); // no longer on the retired gen-2 row
        Assert.All(membershipHoldIds, id => Assert.Contains(id, liveHoldIds));

        // Still an ACTIVE boulder (not frozen) and NOT spuriously flagged for review — a plain Carried
        // decision must never force review.
        Assert.False(spanBoulder.IsHistoric);
        Assert.False(spanBoulder.NeedsReview);
    }

    // The persisted state behind the read: the centre membership is repointed (delete+insert) to the gen-3
    // twin, the right membership is untouched at gen-2, the boulder is bumped to the new generation and
    // stays active. Guards the merge/idempotency contract too — exactly one membership per successor.
    [Fact]
    public async Task AfterCentreOnlyPromote_SpanningBoulder_PartiallyRepointed_InStore()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallWithSpanningBoulderAsync(h);
        var (_, stagedHoldId) = await StageCentreUpdateAsync(h, w.WallId);

        await NewBigUpdateService(h).PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, stagedHoldId)));

        await using var db = h.CreateContext();

        var membershipHoldIds = await db.BoulderHolds
            .Where(bh => bh.BoulderId == w.SpanningBoulderId)
            .Select(bh => bh.HoldId)
            .ToListAsync();
        Assert.Equal(2, membershipHoldIds.Count);
        Assert.Contains(stagedHoldId, membershipHoldIds);
        Assert.Contains(w.RightHoldId, membershipHoldIds);

        var spanBoulder = await db.Boulders.SingleAsync(b => b.Id == w.SpanningBoulderId);
        Assert.Equal(3, spanBoulder.Generation);
        Assert.False(spanBoulder.IsHistoric);
        Assert.False(spanBoulder.NeedsReview);
    }

    private static WallBigUpdateService NewBigUpdateService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

    private static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    // Seeds a live gen-2 wall of TWO panels — centre (0,0) and right (1,0) — each with a live hold, plus a
    // boulder SPANNING both panels (one centre hold AND one right hold). Real WallPanelIds, which per-panel
    // scoping keys on.
    private static async Task<SpanningWall> SeedTwoPanelWallWithSpanningBoulderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic",
            OwnerId = h.Owner.Id,
            CurrentGeneration = 2,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", Generation = 2 };
        var right = new WallPanel { WallId = wall.Id, Col = 1, Row = 0, Photo = [4, 5, 6], PhotoContentType = "image/jpeg", Generation = 2 };
        db.WallPanels.AddRange(centre, right);

        var centreHold = new Hold { WallId = wall.Id, WallPanelId = centre.Id, X = 0.30, Y = 0.30, Radius = 0.02, Generation = 2 };
        var rightHold = new Hold { WallId = wall.Id, WallPanelId = right.Id, X = 0.70, Y = 0.40, Radius = 0.02, Generation = 2 };
        db.Holds.AddRange(centreHold, rightHold);

        var spanBoulder = new Boulder { WallId = wall.Id, Name = "Span", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(spanBoulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = spanBoulder.Id, HoldId = centreHold.Id });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = spanBoulder.Id, HoldId = rightHold.Id });

        await db.SaveChangesAsync();
        return new SpanningWall(wall.Id, centre.Id, right.Id, centreHold.Id, rightHold.Id, spanBoulder.Id);
    }

    // Stages a centre-only update: a fresh centre panel at gen 3 with one staged detection, exactly as
    // StartAsync would leave the DB before promote.
    private static async Task<(Guid PanelId, Guid StagedHoldId)> StageCentreUpdateAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = wallId,
            Col = 0,
            Row = 0,
            Photo = null,
            StagedPhoto = [7, 8, 9],
            StagedPhotoContentType = "image/jpeg",
            Generation = 3,
        };
        db.WallPanels.Add(panel);

        var staged = new Hold
        {
            WallId = wallId,
            WallPanelId = panel.Id,
            X = 0.31,
            Y = 0.31,
            Radius = 0.02,
            Generation = 3,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(staged);
        await db.SaveChangesAsync();
        return (panel.Id, staged.Id);
    }

    private sealed record SpanningWall(
        Guid WallId,
        Guid CentrePanelId,
        Guid RightPanelId,
        Guid CentreHoldId,
        Guid RightHoldId,
        Guid SpanningBoulderId);
}
