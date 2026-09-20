using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The decision rows: that all three kinds survive a lost browser session, and that a decision about a
/// staged hold deleted mid-session cleans itself up instead of dangling.
/// </summary>
public class WallUpdateSessionDecisionTests
{
    [Fact]
    public async Task AllThreeDecisionKinds_PersistAndReadBackAsAPromoteReadyConfirmation()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var neighbourId = await WallUpdateSessionFixture.PanelIdAsync(h, 1, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.4, 0.4);
        var brandNew = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.6, 0.6);
        var dropped = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1, 0.7, 0.7);
        var neighbourHold = await WallUpdateSessionFixture.AddStagedHoldAsync(h, neighbourId, 1, 0.2, 0.2);
        var absentHold = await WallUpdateSessionFixture.AddStagedHoldAsync(h, neighbourId, 1, 0.3, 0.3);
        var sessions = WallUpdateSessionFixture.Sessions(h);

        await sessions.SaveCarryOutcomeAsync(
            h.WallId,
            [new CarryoverDecision(old.Id, CarryKind.Changed, twin)],
            [brandNew],
            [dropped]);
        await sessions.SaveNeighbourLinkSetAsync(
            h.WallId,
            new NeighbourLinkSet(neighbourId, [new ConfirmedLink(twin, neighbourHold, Moved: true)], [absentHold]));

        var read = await sessions.GetDecisionsAsync(h.WallId);
        var carry = Assert.Single(read.Carryover);
        Assert.Equal(old.Id, carry.OldHoldId);
        Assert.Equal(CarryKind.Changed, carry.Kind);
        Assert.Equal(twin, carry.NewHoldId);
        Assert.Equal([brandNew], read.AcceptedNewCenterHoldIds);
        Assert.Equal([dropped], read.RemovedNewCenterHoldIds);
        var set = Assert.Single(read.Neighbours);
        Assert.Equal(neighbourId, set.PanelId);
        var link = Assert.Single(set.Links);
        Assert.Equal(twin, link.NeighborHoldId);
        Assert.Equal(neighbourHold, link.NewHoldId);
        Assert.True(link.Moved);
        Assert.Equal([absentHold], set.RemovedNeighbourHoldIds);

        // Deliberately not persisted: the matcher recomputes them from the staged photos on resume.
        Assert.Null(read.CarriedWarpPositions);
        Assert.Null(read.CarriedWarpShapes);
    }

    [Fact]
    public async Task SingleDecisionUpserts_OverwriteRatherThanAppend()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1);
        var sessions = WallUpdateSessionFixture.Sessions(h);

        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Carried, twin));
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old.Id, CarryKind.Removed, twin));
        await sessions.SaveNewCentreHoldDecisionAsync(h.WallId, twin, discarded: true);
        await sessions.SaveNewCentreHoldDecisionAsync(h.WallId, twin, discarded: false);

        var read = await sessions.GetDecisionsAsync(h.WallId);
        var carry = Assert.Single(read.Carryover);
        Assert.Equal(CarryKind.Removed, carry.Kind);
        Assert.Null(carry.NewHoldId);
        Assert.Equal([twin], read.AcceptedNewCenterHoldIds);
        Assert.Empty(read.RemovedNewCenterHoldIds);
    }

    [Fact]
    public async Task DeletingAStagedHold_CascadesEveryDecisionRowThatReferencedIt()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());
        var centreId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var neighbourId = await WallUpdateSessionFixture.PanelIdAsync(h, 1, 0);
        var twin = await WallUpdateSessionFixture.AddStagedHoldAsync(h, centreId, 1);
        var neighbourHold = await WallUpdateSessionFixture.AddStagedHoldAsync(h, neighbourId, 1, 0.2, 0.2);
        var sessions = WallUpdateSessionFixture.Sessions(h);
        await sessions.SaveCarryOutcomeAsync(h.WallId, [new CarryoverDecision(old.Id, CarryKind.Carried, twin)], [twin], []);
        await sessions.SaveNeighbourLinkSetAsync(
            h.WallId, new NeighbourLinkSet(neighbourId, [new ConfirmedLink(twin, neighbourHold, false)], []));

        // The twin is BOTH a carry decision's paired hold, a new-centre subject and a link's centre end.
        await WallUpdateSessionFixture.Panels(h).DeleteStagedHoldAsync(h.WallId, twin);

        await using var db = h.CreateContext();
        Assert.False(await db.WallUpdateHoldDecisions.AnyAsync(d => d.HoldId == twin || d.PairedHoldId == twin));
        Assert.False(await db.WallUpdateNeighbourDecisions.AnyAsync(d => d.CentreHoldId == twin));
        var read = await sessions.GetDecisionsAsync(h.WallId);
        Assert.Empty(read.Carryover);
        Assert.Empty(read.AcceptedNewCenterHoldIds);
        Assert.Empty(read.Neighbours);
    }

    [Fact]
    public async Task DecisionsAboutAHoldThatHasSinceGone_AreDroppedRatherThanBreakingTheSave()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var sessions = WallUpdateSessionFixture.Sessions(h);

        await sessions.SaveCarryOutcomeAsync(
            h.WallId,
            [new CarryoverDecision(old.Id, CarryKind.Carried, Guid.NewGuid())],
            [Guid.NewGuid()],
            []);

        var read = await sessions.GetDecisionsAsync(h.WallId);
        var carry = Assert.Single(read.Carryover);
        Assert.Null(carry.NewHoldId);
        Assert.Empty(read.AcceptedNewCenterHoldIds);
    }
}
