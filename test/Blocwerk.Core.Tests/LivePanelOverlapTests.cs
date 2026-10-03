// <copyright file="LivePanelOverlapTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.PanelUpdateCarryFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Staging a single new panel matches it against the LIVE neighbour at each adjacent position: never a
/// superseded row (history), and with the live panel's holds even when a subset update left it at an
/// older generation than the wall.
/// </summary>
public class LivePanelOverlapTests
{
    // After update 1 (re-shoots (0,0) and (1,0)), (2,0) is live at gen 2 while the wall is gen 3. A panel
    // staged at (3,0) must be offered the far hold. NON-VACUOUS: the neighbour read was gen == 3, so none.
    [Fact]
    public async Task StagingNextToASkippedPanel_ProposesItsOlderGenerationHolds()
    {
        using var h = new WallTestHarness();
        var w = await SeedAfterUpdateOneAsync(h);
        var matcher = new IndexAlignedTestMatcher();

        var result = await PanelService(h, matcher).StagePanelAsync(w.WallId, 3, 0, [9], "image/jpeg");

        var proposal = Assert.Single(result.Proposals);
        Assert.Equal(w.PanelIds[2], proposal.NeighborPanelId);
        Assert.Equal(w.FarHoldId, proposal.HoldAId);
    }

    // A panel staged at (1,1) sits next to (1,0), which has a superseded gen-2 row and a live gen-3 one. Only
    // the live photo is matched. NON-VACUOUS: every row with a photo was iterated, superseded ones included.
    [Fact]
    public async Task StagingNextToAReShotPanel_MatchesOnlyItsLiveRow()
    {
        using var h = new WallTestHarness();
        var w = await SeedAfterUpdateOneAsync(h);
        var matcher = new IndexAlignedTestMatcher();

        var result = await PanelService(h, matcher).StagePanelAsync(w.WallId, 1, 1, [9], "image/jpeg");

        var image = Assert.Single(matcher.LeftImages);
        Assert.Equal(new byte[] { 31 }, image);
        Assert.DoesNotContain(result.Proposals, p => p.NeighborPanelId == w.PanelIds[1]);
    }

    private static async Task<RowWall> SeedAfterUpdateOneAsync(WallTestHarness h)
    {
        var w = await SeedAsync(h);
        var first = await StageAsync(h, w.WallId, 3, 0, 1);
        await Service(h).PromoteAsync(w.WallId, Confirm(
            new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, first[0].HoldId),
            new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, first[1].HoldId)));
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold> { new(0.20, 0.40, 0.02, null, 0.9) }));
        return w;
    }

    private static WallPanelService PanelService(WallTestHarness h, IHoldOverlapMatcher matcher) =>
        new(h.DbContextFactory, h.CurrentUser, h.HoldDetection, matcher, NullLogger<WallPanelService>.Instance);
}
