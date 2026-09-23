using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the neighbour-panel carryover dedup fix. Before the fix, twin-matching was CENTRE-ONLY:
/// a co-updated neighbour's old holds were matched only against the staged CENTRE, never found a twin,
/// and were cloned onto their own panel — while <see cref="WallBigUpdateService"/> also promoted the
/// neighbour's FRESH staged detections. Net: every physical neighbour hold ended up TWICE (clone + fresh).
/// <para>
/// These tests assert COUNTS (not just placement — the existing suite missed the duplicate): a matched
/// neighbour old hold promotes its SAME-panel twin in place (one gen-N+1 row, lineage link), a genuinely
/// unmatched old clones once, and an unmatched-new detection appears once. They are NON-VACUOUS — each
/// count fails against the pre-fix clone-everything code (which produced one extra row per neighbour hold).
/// </para>
/// </summary>
public class NeighbourCarryDedupTests
{
    // Headline: centre + (1,0) neighbour promote where the neighbour's old hold twin-matches its own
    // staged detection. The neighbour panel must carry EXACTLY ONE gen-3 hold — the promoted twin — with a
    // Same lineage link and NO clone. NON-VACUOUS: pre-fix the neighbour old cloned (twin not in the
    // centre-only lookup) AND the detection was kept, so the neighbour panel held two gen-3 rows.
    [Fact]
    public async Task NeighbourOldHold_TwinMatched_PromotesInPlace_NoDuplicate()
    {
        using var h = new WallTestHarness();
        var w = await SeedThreePanelWallAsync(h);
        var s = await StageCentrePlusNeighbourAsync(h, w.WallId);

        await PromoteService(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s.CentreStagedHoldId),
            new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s.NeighbourStagedHoldId)));

        await using var db = h.CreateContext();

        // EXACTLY ONE gen-3 hold on the neighbour panel — the twin, in place. This is the dedup assertion.
        var neighbourGen3 = await db.Holds
            .Where(x => x.WallPanelId == s.NeighbourNewPanelId && x.Generation == 3)
            .Select(x => x.Id)
            .ToListAsync();
        Assert.Single(neighbourGen3);
        Assert.Equal(s.NeighbourStagedHoldId, neighbourGen3[0]);

        // The neighbour old hold carried its lineage onto that ONE twin (Same), with no second successor.
        var links = await db.HoldGenerationLinks.Where(l => l.OldHoldId == w.NeighbourHoldId).ToListAsync();
        Assert.Single(links);
        Assert.Equal(s.NeighbourStagedHoldId, links[0].NewHoldId);
        Assert.Equal(HoldGenerationLinkKind.Same, links[0].Kind);

        // The neighbour-only boulder advanced onto the twin (one membership, no stale clone pointer).
        var membership = await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.NeighbourBoulderId);
        Assert.Equal(s.NeighbourStagedHoldId, membership.HoldId);

        // Non-updated far panel untouched; live read shows the twin, not the retired old row.
        Assert.Equal(2, (await db.Holds.SingleAsync(x => x.Id == w.FarHoldId)).Generation);
        var wall = await h.WallService.GetWallAsync(w.WallId);
        var liveIds = wall!.Holds.Select(x => x.Id).ToHashSet();
        Assert.Contains(s.NeighbourStagedHoldId, liveIds);
        Assert.DoesNotContain(w.NeighbourHoldId, liveIds);
        Assert.Equal(3, wall.Holds.Count); // centre twin + neighbour twin + far hold — no duplicate.
    }

    // A neighbour panel with a MATCHED old (A→A'), a genuinely UNMATCHED old (B, clones), and an
    // UNMATCHED-NEW staged detection (C', kept). The panel must end with EXACTLY THREE gen-3 holds — one
    // per physical hold — never four. NON-VACUOUS: pre-fix A also cloned (A-clone + A' kept), giving four.
    [Fact]
    public async Task NeighbourPanel_MatchedPlusUnmatchedOldPlusNewDetection_EachAppearsOnce()
    {
        using var h = new WallTestHarness();
        var w = await SeedThreePanelWallAsync(h);
        var s = await StageCentrePlusNeighbourAsync(h, w.WallId);

        // A second old hold B on the neighbour (unmatched), and a second staged detection C' (new).
        Guid unmatchedOldB;
        Guid newDetectionC;
        await using (var seed = h.CreateContext())
        {
            var b = new Hold
            {
                WallId = w.WallId, WallPanelId = w.NeighbourPanelId, X = 0.62, Y = 0.62,
                Radius = 0.02, Generation = 2,
            };
            var c = new Hold
            {
                WallId = w.WallId, WallPanelId = s.NeighbourNewPanelId, X = 0.70, Y = 0.70,
                Radius = 0.02, Generation = 3, IsAutoDetected = true, NeedsReview = true,
            };
            seed.Holds.AddRange(b, c);
            await seed.SaveChangesAsync();
            unmatchedOldB = b.Id;
            newDetectionC = c.Id;
        }

        await PromoteService(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s.CentreStagedHoldId),
            new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s.NeighbourStagedHoldId),
            new CarryoverDecision(unmatchedOldB, CarryKind.Carried, null)));

        await using var db = h.CreateContext();

        var neighbourGen3 = await db.Holds
            .Where(x => x.WallPanelId == s.NeighbourNewPanelId && x.Generation == 3)
            .Select(x => x.Id)
            .ToListAsync();
        Assert.Equal(3, neighbourGen3.Count);

        // A → A' in place (the twin), no separate clone.
        var aLinks = await db.HoldGenerationLinks.Where(l => l.OldHoldId == w.NeighbourHoldId).ToListAsync();
        Assert.Single(aLinks);
        Assert.Equal(s.NeighbourStagedHoldId, aLinks[0].NewHoldId);
        Assert.Contains(s.NeighbourStagedHoldId, neighbourGen3);

        // B → a single clone on the neighbour panel (at B's old position, no twin).
        var bLinks = await db.HoldGenerationLinks.Where(l => l.OldHoldId == unmatchedOldB).ToListAsync();
        Assert.Single(bLinks);
        var bClone = await db.Holds.SingleAsync(x => x.Id == bLinks[0].NewHoldId);
        Assert.Equal(s.NeighbourNewPanelId, bClone.WallPanelId);
        Assert.NotEqual(newDetectionC, bClone.Id);
        Assert.Contains(bClone.Id, neighbourGen3);

        // C' — the genuinely new detection — is kept exactly once, with no lineage link.
        Assert.Contains(newDetectionC, neighbourGen3);
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.NewHoldId == newDetectionC));
    }

    // FULL all-panel promote (every panel re-shot) with a neighbour that has old holds — the flow pending
    // merge. Two panels both re-photographed; each old hold twin-matches its own panel's staged detection.
    // Exactly TWO gen-3 holds total (one per panel), no neighbour duplication. NON-VACUOUS: pre-fix the
    // neighbour old cloned while its detection was kept, giving three gen-3 rows.
    [Fact]
    public async Task FullAllPanelPromote_NeighbourWithOldHolds_NoDuplication()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallAsync(h);
        var s = await StageBothPanelsAsync(h, w.WallId);

        await PromoteService(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s.CentreStagedHoldId),
            new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, s.NeighbourStagedHoldId)));

        await using var db = h.CreateContext();

        // Exactly one gen-3 hold per panel; two in total — no duplicate anywhere.
        Assert.Equal(2, await db.Holds.CountAsync(x => x.Generation == 3));
        Assert.Equal(1, await db.Holds.CountAsync(x => x.WallPanelId == s.CentreNewPanelId && x.Generation == 3));
        Assert.Equal(1, await db.Holds.CountAsync(x => x.WallPanelId == s.NeighbourNewPanelId && x.Generation == 3));

        // Each old hold has a single Same lineage link onto its own panel's twin.
        Assert.Equal(s.NeighbourStagedHoldId,
            (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.NeighbourHoldId)).NewHoldId);
        Assert.Equal(s.CentreStagedHoldId,
            (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.CentreHoldId)).NewHoldId);
    }

    // Session build (BuildSessionAsync via ResumeAsync): the per-panel matcher pass must produce a carryover
    // proposal for the NEIGHBOUR's old hold mapping to the neighbour's OWN staged detection — the seed the
    // UI needs to build the correct promote confirmation. NON-VACUOUS: pre-fix all olds matched only the
    // centre staged, so no proposal ever pointed a neighbour old at a neighbour staged hold.
    [Fact]
    public async Task BuildSession_ProducesNeighbourCarryoverProposal_AgainstOwnPanel()
    {
        using var h = new WallTestHarness();
        var w = await SeedThreePanelWallAsync(h);
        var s = await StageCentrePlusNeighbourAsync(h, w.WallId);

        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection,
            new IndexAlignedMatcher(), NullLogger<WallBigUpdateService>.Instance);

        var session = await service.ResumeAsync(w.WallId);

        // A neighbour old → neighbour staged proposal exists (its twin is on its OWN panel, not the centre).
        Assert.Contains(session.Carryover, p =>
            p.OldHoldId == w.NeighbourHoldId && p.NewHoldId == s.NeighbourStagedHoldId);

        // The centre proposal is still centre-scoped.
        Assert.Contains(session.Carryover, p =>
            p.OldHoldId == w.CentreHoldId && p.NewHoldId == s.CentreStagedHoldId);

        // The neighbour old never proposes a CENTRE staged hold as its twin.
        Assert.DoesNotContain(session.Carryover, p =>
            p.OldHoldId == w.NeighbourHoldId && p.NewHoldId == s.CentreStagedHoldId);
    }

    private static WallBigUpdateService PromoteService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

    private static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    // Live gen-2 wall: centre (0,0), neighbour (1,0), non-updated far (2,0). Each panel one hold; a
    // neighbour-only boulder and a boulder spanning the neighbour + far panel (for repoint coverage).
    private static async Task<ThreePanelWall> SeedThreePanelWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic", OwnerId = h.Owner.Id, CurrentGeneration = 2,
            Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var neighbour = new WallPanel { WallId = wall.Id, Col = 1, Row = 0, Photo = [2], PhotoContentType = "image/jpeg", Generation = 2 };
        var far = new WallPanel { WallId = wall.Id, Col = 2, Row = 0, Photo = [3], PhotoContentType = "image/jpeg", Generation = 2 };
        db.WallPanels.AddRange(centre, neighbour, far);

        var centreHold = new Hold { WallId = wall.Id, WallPanelId = centre.Id, X = 0.30, Y = 0.30, Radius = 0.02, Generation = 2 };
        var neighbourHold = new Hold { WallId = wall.Id, WallPanelId = neighbour.Id, X = 0.55, Y = 0.40, Radius = 0.02, Generation = 2 };
        var farHold = new Hold { WallId = wall.Id, WallPanelId = far.Id, X = 0.80, Y = 0.50, Radius = 0.02, Generation = 2 };
        db.Holds.AddRange(centreHold, neighbourHold, farHold);

        var nbBoulder = new Boulder { WallId = wall.Id, Name = "Neighbour", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(nbBoulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = nbBoulder.Id, HoldId = neighbourHold.Id });

        var spanBoulder = new Boulder { WallId = wall.Id, Name = "Span", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(spanBoulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = spanBoulder.Id, HoldId = neighbourHold.Id });
        db.BoulderHolds.Add(new BoulderHold { BoulderId = spanBoulder.Id, HoldId = farHold.Id });

        await db.SaveChangesAsync();
        return new ThreePanelWall(
            wall.Id, centre.Id, neighbour.Id, far.Id,
            centreHold.Id, neighbourHold.Id, farHold.Id, nbBoulder.Id, spanBoulder.Id);
    }

    // Stages a centre + (1,0) neighbour update: fresh gen-3 panels at (0,0) and (1,0), each with one staged
    // detection, as StartAsync would leave the DB before promote. The (2,0) far panel is NOT re-shot.
    private static async Task<StagedUpdate> StageCentrePlusNeighbourAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();

        var centrePanel = new WallPanel
        {
            WallId = wallId, Col = 0, Row = 0, Photo = null,
            StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        var neighbourPanel = new WallPanel
        {
            WallId = wallId, Col = 1, Row = 0, Photo = null,
            StagedPhoto = [8], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(centrePanel, neighbourPanel);

        var centreStaged = new Hold
        {
            WallId = wallId, WallPanelId = centrePanel.Id, X = 0.31, Y = 0.31, Radius = 0.02,
            Generation = 3, IsAutoDetected = true, NeedsReview = true,
        };
        var neighbourStaged = new Hold
        {
            WallId = wallId, WallPanelId = neighbourPanel.Id, X = 0.56, Y = 0.41, Radius = 0.02,
            Generation = 3, IsAutoDetected = true, NeedsReview = true,
        };
        db.Holds.AddRange(centreStaged, neighbourStaged);

        await db.SaveChangesAsync();
        return new StagedUpdate(centrePanel.Id, neighbourPanel.Id, centreStaged.Id, neighbourStaged.Id);
    }

    // Live gen-2 wall: centre (0,0) + neighbour (1,0), each one hold — for the full all-panel promote.
    private static async Task<TwoPanelWall> SeedTwoPanelWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic", OwnerId = h.Owner.Id, CurrentGeneration = 2,
            Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var neighbour = new WallPanel { WallId = wall.Id, Col = 1, Row = 0, Photo = [2], PhotoContentType = "image/jpeg", Generation = 2 };
        db.WallPanels.AddRange(centre, neighbour);

        var centreHold = new Hold { WallId = wall.Id, WallPanelId = centre.Id, X = 0.30, Y = 0.30, Radius = 0.02, Generation = 2 };
        var neighbourHold = new Hold { WallId = wall.Id, WallPanelId = neighbour.Id, X = 0.55, Y = 0.40, Radius = 0.02, Generation = 2 };
        db.Holds.AddRange(centreHold, neighbourHold);

        await db.SaveChangesAsync();
        return new TwoPanelWall(wall.Id, centre.Id, neighbour.Id, centreHold.Id, neighbourHold.Id);
    }

    // Stages BOTH panels (centre + (1,0)) at gen 3, each with one staged detection.
    private static async Task<StagedUpdate> StageBothPanelsAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();

        var centrePanel = new WallPanel
        {
            WallId = wallId, Col = 0, Row = 0, Photo = null,
            StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        var neighbourPanel = new WallPanel
        {
            WallId = wallId, Col = 1, Row = 0, Photo = null,
            StagedPhoto = [8], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(centrePanel, neighbourPanel);

        var centreStaged = new Hold
        {
            WallId = wallId, WallPanelId = centrePanel.Id, X = 0.31, Y = 0.31, Radius = 0.02,
            Generation = 3, IsAutoDetected = true, NeedsReview = true,
        };
        var neighbourStaged = new Hold
        {
            WallId = wallId, WallPanelId = neighbourPanel.Id, X = 0.56, Y = 0.41, Radius = 0.02,
            Generation = 3, IsAutoDetected = true, NeedsReview = true,
        };
        db.Holds.AddRange(centreStaged, neighbourStaged);

        await db.SaveChangesAsync();
        return new StagedUpdate(centrePanel.Id, neighbourPanel.Id, centreStaged.Id, neighbourStaged.Id);
    }

    /// <summary>
    /// A deterministic stand-in for the OpenCV matcher: proposes left[i] ↔ right[i] for the overlapping
    /// prefix and leaves the rest unmatched. Lets a session-build test exercise the per-panel carryover
    /// pass without native OpenCV — the real matcher's geometry is covered elsewhere.
    /// </summary>
    private sealed class IndexAlignedMatcher : IHoldOverlapMatcher
    {
        public HoldOverlapResult Match(
            byte[] leftImage,
            IReadOnlyList<MatcherHold> leftHolds,
            byte[] rightImage,
            IReadOnlyList<MatcherHold> rightHolds,
            HoldOverlapDirection direction,
            ILogger? diag = null,
            HoldOverlapSeed? seed = null)
        {
            var n = Math.Min(leftHolds.Count, rightHolds.Count);
            var proposals = new List<HoldOverlapProposal>();
            for (var i = 0; i < n; i++)
            {
                proposals.Add(new HoldOverlapProposal(leftHolds[i].Id, rightHolds[i].Id, 0.9, false, 1.0, null));
            }

            var unmatchedLeft = leftHolds.Skip(n).Select(hold => hold.Id).ToList();
            var unmatchedRight = rightHolds.Skip(n).Select(hold => hold.Id).ToList();
            return new HoldOverlapResult(proposals, unmatchedLeft, unmatchedRight);
        }
    }

    private sealed record ThreePanelWall(
        Guid WallId,
        Guid CentrePanelId,
        Guid NeighbourPanelId,
        Guid FarPanelId,
        Guid CentreHoldId,
        Guid NeighbourHoldId,
        Guid FarHoldId,
        Guid NeighbourBoulderId,
        Guid SpanBoulderId);

    private sealed record TwoPanelWall(
        Guid WallId,
        Guid CentrePanelId,
        Guid NeighbourPanelId,
        Guid CentreHoldId,
        Guid NeighbourHoldId);

    private sealed record StagedUpdate(
        Guid CentreNewPanelId,
        Guid NeighbourNewPanelId,
        Guid CentreStagedHoldId,
        Guid NeighbourStagedHoldId);
}
