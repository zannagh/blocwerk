// <copyright file="AlignmentFailureTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.AlignmentFailureScenario;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A panel whose new photo the matcher cannot line up with the previous one carries its old holds at their
/// OLD coordinates. That must never be silent: the session says so per panel (and keeps saying so on a
/// resume), nothing counts as confirmed, and the promote flags the blind carries — but no boulder.
/// </summary>
public class AlignmentFailureTests
{
    [Fact]
    public async Task CentreFailure_FlagsThePanel_ProposesNothing_AndPersistsIt()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h);

        var session = await s.BigUpdate(new UnalignableHoldMatcher((l, _) => Same(l, OldWallPhoto))).ResumeAsync(s.WallId);

        Assert.Equal(AutoMatchStatus.Failed, session.AutoMatchStatus);
        Assert.DoesNotContain(session.Carryover, p => p.OldHoldId == s.OldC);
        Assert.Contains(s.OldC, session.RemovedCandidateHoldIds);
        var centre = session.CarriedPanels!.Single(p => p.Col == 0);
        Assert.True(centre.AlignmentFailed);
        Assert.False(session.CarriedPanels!.Single(p => p.Col == 1).AlignmentFailed);

        await using var db = h.CreateContext();
        var row = await db.WallUpdateSessions.SingleAsync();
        Assert.Equal([s.StagedCentrePanelId], row.UnalignedCarryPanelIds);
        Assert.Empty(row.UnalignedOverlapPanelIds);
        Assert.Empty(await WallUpdateSessionFixture.Sessions(h).GetCarryConfirmationsAsync(s.WallId));
    }

    // RANSAC is not deterministic: a later run may pass. The resumed session must still show what the user
    // reviewed — the panel stays unaligned and the matcher is not even asked about it again.
    [Fact]
    public async Task CentreFailure_SurvivesAResumeWhoseMatcherWouldSucceed()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h);
        await s.BigUpdate(new UnalignableHoldMatcher((l, _) => Same(l, OldWallPhoto))).ResumeAsync(s.WallId);

        var healthy = new UnalignableHoldMatcher((_, _) => false);
        var resumed = await s.BigUpdate(healthy).ResumeAsync(s.WallId);

        Assert.True(resumed.CarriedPanels!.Single(p => p.Col == 0).AlignmentFailed);
        Assert.Equal(AutoMatchStatus.Failed, resumed.AutoMatchStatus);
        Assert.DoesNotContain(resumed.Carryover, p => p.OldHoldId == s.OldC);
        Assert.Contains(resumed.Carryover, p => p.OldHoldId == s.OldN);
        Assert.Equal(2, healthy.Calls);
    }

    // The owner's real case: the RIGHT panel's reframed photo failed and its 169 holds went live 743 px off
    // with no sign. Now its blind carry is flagged NeedsReview on promote; its boulder is left alone.
    [Fact]
    public async Task NeighbourFailure_FlagsItsBlindCarriesOnPromote_NotItsBoulder()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h);
        var matcher = new UnalignableHoldMatcher((l, _) => Same(l, OldRight));

        var session = await s.BigUpdate(matcher).ResumeAsync(s.WallId);
        Assert.True(session.CarriedPanels!.Single(p => p.Col == 1).AlignmentFailed);
        Assert.False(session.CarriedPanels!.Single(p => p.Col == 0).AlignmentFailed);
        Assert.Contains(s.OldN, session.RemovedCandidateHoldIds);
        Assert.Contains(session.Carryover, p => p.OldHoldId == s.OldC && p.NewHoldId == s.NewC);

        await s.ContinueAsync(session);
        await s.PromoteAsync(matcher);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldN);
        var carried = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.True(carried.NeedsReview);
        Assert.Equal((0.60, 0.60), (carried.X, carried.Y));
        Assert.Equal(s.StagedRightPanelId, carried.WallPanelId);
        Assert.False((await db.Holds.SingleAsync(x => x.Id == s.NewC)).NeedsReview);

        var boulder = await db.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        Assert.False(boulder.NeedsReview);
        Assert.False(boulder.IsHistoric);
    }

    [Fact]
    public async Task OverlapFailure_FlagsTheNeighbourStep_AndPersistsIt()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h);
        var matcher = new UnalignableHoldMatcher((l, r) => Same(l, StagedCentre) && Same(r, StagedRight));

        var session = await s.BigUpdate(matcher).ResumeAsync(s.WallId);

        var neighbour = Assert.Single(session.Neighbours);
        Assert.True(neighbour.AlignmentFailed);
        Assert.Empty(neighbour.Proposals);
        Assert.All(session.CarriedPanels!, p => Assert.False(p.AlignmentFailed));
        await using var db = h.CreateContext();
        Assert.Equal([s.StagedRightPanelId], (await db.WallUpdateSessions.SingleAsync()).UnalignedOverlapPanelIds);
    }

    // Control: when everything lines up nothing is flagged, nothing is persisted, and the promote is as before.
    [Fact]
    public async Task SuccessfulMatch_ChangesNothing()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h);
        var matcher = new UnalignableHoldMatcher((_, _) => false);

        var session = await s.BigUpdate(matcher).ResumeAsync(s.WallId);
        Assert.Equal(AutoMatchStatus.Ok, session.AutoMatchStatus);
        Assert.All(session.CarriedPanels!, p => Assert.False(p.AlignmentFailed));
        Assert.All(session.Neighbours, n => Assert.False(n.AlignmentFailed));
        Assert.Equal(2, session.Carryover.Count);

        await s.ContinueAsync(session);
        await s.PromoteAsync(matcher);

        await using var db = h.CreateContext();
        var closed = await db.WallUpdateSessions.SingleAsync();
        Assert.Empty(closed.UnalignedCarryPanelIds);
        Assert.Empty(closed.UnalignedOverlapPanelIds);
        Assert.False((await db.Holds.SingleAsync(x => x.Id == s.NewN)).NeedsReview);
        Assert.False((await db.Holds.SingleAsync(x => x.Id == s.NewC)).NeedsReview);
    }

    [Fact]
    public async Task StagingAPanel_ReportsTheNeighbourItCouldNotAlignWith()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h);
        WallUpdateSessionFixture.NoDetections(h);
        await using (var db = h.CreateContext())
        {
            // Close the big update so the stand-alone add-panel flow is what runs.
            await db.WallUpdateSessions.ExecuteDeleteAsync();
            await db.Holds.Where(x => x.Generation == 3).ExecuteDeleteAsync();
            await db.WallPanels.Where(p => p.Generation == 3).ExecuteDeleteAsync();
        }

        var panels = new WallPanelService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection,
            new UnalignableHoldMatcher((_, _) => true),
            NullLogger<WallPanelService>.Instance);
        var result = await panels.StagePanelAsync(s.WallId, 0, 1, [9], "image/jpeg");

        Assert.Empty(result.Proposals);
        Assert.NotEmpty(result.UnalignedNeighbourIds!);
    }
}
