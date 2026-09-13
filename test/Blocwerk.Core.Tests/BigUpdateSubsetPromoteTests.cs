using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for SUBSET (per-panel) promote: a big-wall update that re-photographs only the centre must
/// touch ONLY the centre. The right panel and its holds stay at their generation (badged outdated via
/// the bumped wall generation), never carried and never duplicated onto the centre. A boulder wholly on
/// the centre advances; a boulder spanning the centre and the un-updated right panel is PARTIALLY
/// repointed — its centre membership advances to the gen-3 twin while its right membership stays at gen-2
/// — so the live read shows both holds (decision D-B, corrected). The center-first rule (D-D) rejects re-photographing
/// an outer panel without the more-central one. Seeds a real 2-panel wall and drives the real
/// <see cref="WallBigUpdateService"/> against the SQLite harness, mirroring <see cref="BigUpdatePromoteTests"/>.
/// </summary>
public class BigUpdateSubsetPromoteTests
{
    // The headline subset invariant: centre-only update leaves the right panel + its holds untouched at
    // the old generation (not carried, not duplicated onto the centre), advances the centre and the
    // centre-only boulder, and PARTIALLY repoints the spanning boulder (centre membership → gen-3 twin,
    // right membership stays at gen-2) so it keeps rendering both holds in the live read.
    [Fact]
    public async Task CentreOnlyUpdate_LeavesRightPanelUntouched_AdvancesCentreOnlyBoulder_SpanningBoulderPartiallyRepointed()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallAsync(h);
        var (_, stagedHoldId) = await StageCentreUpdateAsync(h, w.WallId);
        var service = NewService(h);

        // Only the centre hold is declared (the review layer never surfaces non-updated-panel holds).
        await service.PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, stagedHoldId)));

        await using var db = h.CreateContext();

        // Wall advanced to the new (target/max) generation.
        var wall = await db.Walls.SingleAsync(x => x.Id == w.WallId);
        Assert.Equal(3, wall.CurrentGeneration);

        // RIGHT PANEL UNTOUCHED: its hold is still the same row, at gen 2, on the same panel.
        var rightRow = await db.Holds.SingleAsync(x => x.Id == w.RightHoldId);
        Assert.Equal(2, rightRow.Generation);
        Assert.Equal(w.RightPanelId, rightRow.WallPanelId);
        // No successor row and no lineage link were ever created for the right hold.
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == w.RightHoldId));
        // The right panel itself stayed at gen 2 (not promoted) and is now outdated vs the wall gen.
        var rightPanel = await db.WallPanels.SingleAsync(p => p.Id == w.RightPanelId);
        Assert.Equal(2, rightPanel.Generation);
        Assert.True(rightPanel.Generation < wall.CurrentGeneration);

        // NOT DUPLICATED ONTO CENTRE: exactly one gen-3 hold exists (the promoted centre twin), so the
        // 169-holds-on-the-centre bug cannot recur — the right hold was never warp-carried across.
        Assert.Equal(1, await db.Holds.CountAsync(x => x.Generation == 3));

        // CENTRE ADVANCED: old gen-2 row retained; the staged twin is the gen-3 successor, linked.
        Assert.Equal(2, (await db.Holds.SingleAsync(x => x.Id == w.CentreHoldId)).Generation);
        var centreLink = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.CentreHoldId);
        Assert.Equal(stagedHoldId, centreLink.NewHoldId);
        Assert.Equal(3, (await db.Holds.SingleAsync(x => x.Id == stagedHoldId)).Generation);

        // CENTRE-ONLY BOULDER survived and advanced onto the successor.
        var centreBoulder = await db.Boulders.SingleAsync(b => b.Id == w.CentreBoulderId);
        Assert.Equal(3, centreBoulder.Generation);
        Assert.False(centreBoulder.IsHistoric);
        Assert.Equal(stagedHoldId, (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.CentreBoulderId)).HoldId);

        // SPANNING BOULDER is PARTIALLY repointed (bug fix): its centre membership advances to the gen-3
        // twin while its right membership stays on the retained gen-2 right hold, so the live read (which
        // resolves each position to its latest live panel) shows BOTH holds instead of dropping the centre
        // one. It stays active and is not spuriously flagged for review (a plain Carried decision). The old
        // assertion here expected the centre membership to stay on the retired gen-2 row — that was exactly
        // the missing-hold bug, so it is updated to the successor.
        var spanBoulder = await db.Boulders.SingleAsync(b => b.Id == w.SpanningBoulderId);
        Assert.Equal(3, spanBoulder.Generation);
        Assert.False(spanBoulder.IsHistoric);
        Assert.False(spanBoulder.NeedsReview);
        var spanMembership = await db.BoulderHolds
            .Where(bh => bh.BoulderId == w.SpanningBoulderId)
            .Select(bh => bh.HoldId)
            .ToListAsync();
        Assert.Equal(2, spanMembership.Count);
        Assert.Contains(stagedHoldId, spanMembership); // centre hold advanced to its gen-3 successor
        Assert.DoesNotContain(w.CentreHoldId, spanMembership); // no longer on the retired gen-2 centre row
        Assert.Contains(w.RightHoldId, spanMembership); // right hold untouched at gen-2
    }

    // A hold ADDED during the manual touch-up step (manual, not auto-detected, and absent from the
    // carryover's accepted-new list — which is computed one step earlier) must still be promoted live,
    // never silently dropped by the new-centre reconcile.
    [Fact]
    public async Task Promote_ManualTouchupCentreHold_NotInAcceptedList_SurvivesLive()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallAsync(h);
        var (panelId, stagedHoldId) = await StageCentreUpdateAsync(h, w.WallId);

        Guid manualId;
        await using (var db = h.CreateContext())
        {
            var manual = new Hold
            {
                WallId = w.WallId,
                WallPanelId = panelId,
                X = 0.5,
                Y = 0.5,
                Radius = 0.02,
                Generation = 3,
                IsAutoDetected = false,
                NeedsReview = false,
            };
            db.Holds.Add(manual);
            await db.SaveChangesAsync();
            manualId = manual.Id;
        }

        var service = NewService(h);

        // The confirmation's accepted-new list is empty — the manual hold is not in it.
        await service.PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, stagedHoldId)));

        await using (var db = h.CreateContext())
        {
            var manual = await db.Holds.SingleAsync(x => x.Id == manualId);
            Assert.Equal(3, manual.Generation);
            Assert.False(manual.NeedsReview);
        }
    }

    // Center-first (D-D): re-photographing the right panel while the centre is not part of the update is
    // rejected up front — an outer panel may never be advanced past its more-central neighbour.
    [Fact]
    public async Task StartAsync_StagingRightPanelWithoutCentre_Rejected()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallAsync(h);
        var service = NewService(h);

        var photos = new List<BigUpdatePhoto> { new([9, 9, 9], "image/jpeg", 1, 0) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(w.WallId, photos));
        Assert.Contains("Center-first", ex.Message);

        // Nothing was staged — the check throws before any panel/hold is written.
        await using var db = h.CreateContext();
        Assert.Equal(2, await db.WallPanels.CountAsync(p => p.WallId == w.WallId));
        Assert.False(await db.WallPanels.AnyAsync(p => p.WallId == w.WallId && p.Generation == 3));
    }

    // FIX A: a crash-mat / floor false hold accepted at an EARLIER generation must be dropped from the
    // CARRIED set on the next update — it never gets a successor and never gets a lineage link — while a
    // mat-signature hold a boulder still references is guarded and carried. The detection-time filter
    // only sees new YOLO detections, so this covers the carry path specifically.
    [Fact]
    public async Task Promote_DropsCarriedMatFalseHolds_ButGuardsBoulderReferencedOne()
    {
        using var h = new WallTestHarness();
        var seed = await SeedMatPopulationWallAsync(h);
        await StageCentreUpdateAsync(h, seed.WallId);
        var service = NewService(h);

        // Empty carryover: the reconcile default-carries every (filtered) old hold, so any hold that
        // still reaches the carry gets a clone + a HoldGenerationLink. A dropped mat gets neither.
        await service.PromoteAsync(seed.WallId, Confirm());

        await using var db = h.CreateContext();

        // NORMAL mid-wall holds were carried: each has a lineage link at the new generation.
        foreach (var normalId in seed.SampleNormalHoldIds)
        {
            Assert.True(
                await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == normalId),
                $"normal hold {normalId} should have been carried");
        }

        // The not-in-boulder mats were DROPPED from the carry: no successor row, no lineage link.
        foreach (var matId in seed.LooseMatHoldIds)
        {
            Assert.False(
                await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == matId),
                $"loose mat hold {matId} must NOT be carried");
        }

        // SAFETY GUARD: the mat-signature hold a boulder references IS still carried (link exists).
        Assert.True(
            await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == seed.BoulderMatHoldId),
            "boulder-referenced mat-signature hold must be guarded and carried");
    }

    private static WallBigUpdateService NewService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

    private static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    // Seeds a live gen-2 wall of TWO panels — centre (0,0) and right (1,0) — each with a live hold, plus
    // a centre-only boulder and a boulder spanning both panels. Unlike the shared harness seed, these
    // holds carry a real WallPanelId, which is what per-panel scoping keys on.
    private static async Task<TwoPanelWall> SeedTwoPanelWallAsync(WallTestHarness h)
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

        var centreBoulder = new Boulder { WallId = wall.Id, Name = "Centre", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(centreBoulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = centreBoulder.Id, HoldId = centreHold.Id });

        var spanBoulder = new Boulder { WallId = wall.Id, Name = "Span", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(spanBoulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = spanBoulder.Id, HoldId = centreHold.Id });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = spanBoulder.Id, HoldId = rightHold.Id });

        await db.SaveChangesAsync();
        return new TwoPanelWall(
            wall.Id, centre.Id, right.Id, centreHold.Id, rightHold.Id, centreBoulder.Id, spanBoulder.Id);
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

    // Seeds a live gen-2 single-panel wall whose centre carries a REALISTIC detection population: ~300
    // normal mid-wall holds (radius ~0.01, Y in 0.10-0.75) so the population-relative mat filter actually
    // runs and the bottom-margin mats land beyond p97, plus four mat-signature holds (Y ~0.82, radius
    // ~0.065). Three of the mats belong to no boulder; one is referenced by a boulder to exercise the
    // guard. Mirrors the synthetic distribution in MatFalseDetectionFilterTests.
    private static async Task<MatPopulationWall> SeedMatPopulationWallAsync(WallTestHarness h)
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
        db.WallPanels.Add(centre);

        var rng = new Random(1234);
        var normalIds = new List<Guid>();
        for (var i = 0; i < 300; i++)
        {
            var radius = 0.006 + (rng.NextDouble() * 0.008);
            var y = 0.10 + (rng.NextDouble() * 0.65);
            var hold = new Hold
            {
                WallId = wall.Id,
                WallPanelId = centre.Id,
                X = Math.Round(rng.NextDouble(), 4),
                Y = Math.Round(y, 4),
                Radius = Math.Round(radius, 4),
                Generation = 2,
            };
            db.Holds.Add(hold);
            normalIds.Add(hold.Id);
        }

        // Four mat-signature holds (over-sized radius in the bottom margin).
        var looseMats = new List<Hold>
        {
            new() { WallId = wall.Id, WallPanelId = centre.Id, X = 0.20, Y = 0.813, Radius = 0.0637, Generation = 2 },
            new() { WallId = wall.Id, WallPanelId = centre.Id, X = 0.45, Y = 0.817, Radius = 0.0662, Generation = 2 },
            new() { WallId = wall.Id, WallPanelId = centre.Id, X = 0.70, Y = 0.821, Radius = 0.0680, Generation = 2 },
        };
        var boulderMat = new Hold { WallId = wall.Id, WallPanelId = centre.Id, X = 0.55, Y = 0.813, Radius = 0.0637, Generation = 2 };
        db.Holds.AddRange(looseMats);
        db.Holds.Add(boulderMat);

        // A boulder references the fourth mat so the guard must keep it in the carry.
        var boulder = new Boulder { WallId = wall.Id, Name = "Guard", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = boulderMat.Id });

        await db.SaveChangesAsync();
        return new MatPopulationWall(
            wall.Id,
            [normalIds[0], normalIds[150], normalIds[299]],
            [looseMats[0].Id, looseMats[1].Id, looseMats[2].Id],
            boulderMat.Id);
    }

    private sealed record MatPopulationWall(
        Guid WallId,
        IReadOnlyList<Guid> SampleNormalHoldIds,
        IReadOnlyList<Guid> LooseMatHoldIds,
        Guid BoulderMatHoldId);

    private sealed record TwoPanelWall(
        Guid WallId,
        Guid CentrePanelId,
        Guid RightPanelId,
        Guid CentreHoldId,
        Guid RightHoldId,
        Guid CentreBoulderId,
        Guid SpanningBoulderId);
}
