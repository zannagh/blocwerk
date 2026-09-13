using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for a MULTI-PANEL subset promote that re-shoots the centre AND a co-updated NEIGHBOUR in the same
/// update. The carry-design bug: <see cref="WallBigUpdateService"/> scoped <c>oldHolds</c> to every updated
/// panel (via IsOnUpdatedPanel), so the neighbour's old holds landed in the centre reconcile loop; having no
/// twin in the centre-only staged set they fell to the clone branch and were cloned onto the CENTRE panel,
/// while <c>PromoteNeighboursAsync</c> independently promoted the neighbour's fresh detections — leaving the
/// neighbour's carried holds on the WRONG panel and boulders pointing at a hold in the wrong place.
/// <para>
/// Invariant now enforced: a carried old hold's successor lives on the NEW-generation row of its OWN
/// (Col,Row). A neighbour's old hold carries onto that neighbour's new-generation panel — never the centre.
/// Seeds a real 3-panel wall (centre, updated neighbour, non-updated neighbour), drives the real promote
/// against the SQLite harness (mirroring <see cref="BigUpdateSubsetPromoteTests"/> /
/// <see cref="SpanningBoulderLiveReadTests"/>), and reads back through <see cref="WallService.GetWallAsync"/>.
/// </para>
/// </summary>
public class MultiPanelSubsetPromoteTests
{
    // Headline: after a centre + (1,0) neighbour promote, the neighbour's carried old hold's successor sits
    // on the (1,0) NEW-generation panel — NOT the centre — with exactly one successor (no duplicate on the
    // centre), the non-updated (2,0) panel is untouched, and every live hold resolves at its panel's live
    // generation through GetWallAsync. NON-VACUOUS: the pre-fix clone-onto-centre put the neighbour successor
    // on the centre new-gen panel, so the WallPanelId assertions below fail against the buggy code.
    [Fact]
    public async Task CentrePlusNeighbourPromote_NeighbourOldHolds_CarryOntoNeighbourPanel_NotCentre()
    {
        using var h = new WallTestHarness();
        var w = await SeedThreePanelWallAsync(h);
        var s = await StageCentrePlusNeighbourAsync(h, w.WallId);

        // Only the centre hold is explicitly declared; the neighbour's old hold is default-carried by the
        // reconcile loop (clone forward) — exactly the path that mis-targeted the centre before the fix.
        await NewService(h).PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s.CentreStagedHoldId)));

        await using var db = h.CreateContext();

        // The wall advanced; the neighbour's old hold has a single gen-3 successor via one lineage link.
        Assert.Equal(3, (await db.Walls.SingleAsync(x => x.Id == w.WallId)).CurrentGeneration);
        var nbLink = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.NeighbourHoldId);
        var nbSuccessor = await db.Holds.SingleAsync(x => x.Id == nbLink.NewHoldId);

        // THE FIX: the successor lives on the (1,0) NEW-generation panel, never on the centre (old or new).
        Assert.Equal(s.NeighbourNewPanelId, nbSuccessor.WallPanelId);
        Assert.NotEqual(s.CentreNewPanelId, nbSuccessor.WallPanelId);
        Assert.NotEqual(w.CentrePanelId, nbSuccessor.WallPanelId);
        Assert.Equal(3, nbSuccessor.Generation);

        // NO DUPLICATE ON CENTRE: nothing derived from the neighbour's old hold sits on the centre panel.
        Assert.False(await db.Holds.AnyAsync(x =>
            x.WallPanelId == s.CentreNewPanelId && x.Id == nbSuccessor.Id));
        Assert.Equal(0, await db.HoldGenerationLinks.CountAsync(l =>
            l.OldHoldId == w.NeighbourHoldId && l.NewHoldId != nbSuccessor.Id));

        // NON-UPDATED (2,0) PANEL UNTOUCHED: its hold is the same row, still gen 2 on the same panel, with no
        // successor and no lineage link; the panel stayed at gen 2 and is now outdated vs the wall.
        var farRow = await db.Holds.SingleAsync(x => x.Id == w.FarHoldId);
        Assert.Equal(2, farRow.Generation);
        Assert.Equal(w.FarPanelId, farRow.WallPanelId);
        Assert.False(await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == w.FarHoldId));
        Assert.Equal(2, (await db.WallPanels.SingleAsync(p => p.Id == w.FarPanelId)).Generation);

        // LIVE READ: each hold resolves at its panel's live generation — centre twin (gen 3), neighbour
        // successor (gen 3, on the neighbour panel) and far hold (gen 2); the retired old rows are gone.
        var wall = await h.WallService.GetWallAsync(w.WallId);
        Assert.NotNull(wall);
        var liveHoldIds = wall!.Holds.Select(x => x.Id).ToHashSet();
        Assert.Contains(s.CentreStagedHoldId, liveHoldIds);
        Assert.Contains(nbSuccessor.Id, liveHoldIds);
        Assert.Contains(w.FarHoldId, liveHoldIds);
        Assert.DoesNotContain(w.CentreHoldId, liveHoldIds);
        Assert.DoesNotContain(w.NeighbourHoldId, liveHoldIds);
    }

    // The boulders: the neighbour-only boulder advances onto the neighbour successor and stays live; the
    // boulder spanning the updated neighbour and the non-updated far panel is PARTIALLY repointed — its
    // neighbour membership advances to the successor while its far membership stays on the retained gen-2 row
    // — and both holds resolve in the live read. Guards against regressing the spanning-boulder partial repoint.
    [Fact]
    public async Task CentrePlusNeighbourPromote_Boulders_AdvanceAndPartiallyRepoint()
    {
        using var h = new WallTestHarness();
        var w = await SeedThreePanelWallAsync(h);
        var s = await StageCentrePlusNeighbourAsync(h, w.WallId);

        await NewService(h).PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s.CentreStagedHoldId)));

        await using var db = h.CreateContext();
        var nbSuccessorId = (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == w.NeighbourHoldId)).NewHoldId;

        // Neighbour-only boulder: advanced to the new generation, active, repointed onto the successor.
        var nbBoulder = await db.Boulders.SingleAsync(b => b.Id == w.NeighbourBoulderId);
        Assert.Equal(3, nbBoulder.Generation);
        Assert.False(nbBoulder.IsHistoric);
        Assert.Equal(nbSuccessorId, (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == w.NeighbourBoulderId)).HoldId);

        // Spanning boulder: neighbour membership → successor, far membership stays at gen 2. Active, not review.
        var spanBoulder = await db.Boulders.SingleAsync(b => b.Id == w.SpanBoulderId);
        Assert.Equal(3, spanBoulder.Generation);
        Assert.False(spanBoulder.IsHistoric);
        Assert.False(spanBoulder.NeedsReview);
        var spanMembership = await db.BoulderHolds
            .Where(bh => bh.BoulderId == w.SpanBoulderId)
            .Select(bh => bh.HoldId)
            .ToListAsync();
        Assert.Equal(2, spanMembership.Count);
        Assert.Contains(nbSuccessorId, spanMembership);
        Assert.Contains(w.FarHoldId, spanMembership);
        Assert.DoesNotContain(w.NeighbourHoldId, spanMembership);

        // Both spanning holds still render in the live read (neither vanishes after the partial promote).
        var wall = await h.WallService.GetWallAsync(w.WallId);
        var liveHoldIds = wall!.Holds.Select(x => x.Id).ToHashSet();
        Assert.Contains(nbSuccessorId, liveHoldIds);
        Assert.Contains(w.FarHoldId, liveHoldIds);
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

    // Seeds a live gen-2 wall of THREE panels — centre (0,0), a neighbour (1,0) and a further, non-updated
    // neighbour (2,0) — each with a live hold, plus a neighbour-only boulder and a boulder spanning (1,0) and
    // (2,0). Real WallPanelIds, which per-panel scoping keys on.
    private static async Task<ThreePanelWall> SeedThreePanelWallAsync(WallTestHarness h)
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
    // detection, exactly as StartAsync would leave the DB before promote. The (2,0) far panel is NOT re-shot.
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

    private sealed record StagedUpdate(
        Guid CentreNewPanelId,
        Guid NeighbourNewPanelId,
        Guid CentreStagedHoldId,
        Guid NeighbourStagedHoldId);
}
