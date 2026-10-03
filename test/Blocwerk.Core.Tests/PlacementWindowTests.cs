// <copyright file="PlacementWindowTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.PanelUpdateCarryFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Whether a re-found twin sits on the old hold's warp-predicted spot is judged in pixels of the new photo,
/// with a window no tighter than the matcher's own acceptance; a twin whose old placement is withheld is
/// flagged for review rather than silently left unplaced.
/// </summary>
public class PlacementWindowTests
{
    // On a 2000 x 1500 landscape photo the twin sits ~85 px from the predicted spot: inside the matcher's gate,
    // so the old placement is kept. NON-VACUOUS: the normalized window (0.04) rejected the 0.05 offset.
    [Fact]
    public async Task TwinWithinTheMatcherGate_KeepsTheOldPlacement()
    {
        var (h, _, twinId) = await PromoteWithWarpAsync(new HoldPositionNorm(0.34, 0.45));
        using (h)
        {
            await using var db = h.CreateContext();
            var twin = await db.Holds.SingleAsync(x => x.Id == twinId);
            Assert.Equal("3", twin.FacetId);
            Assert.False(twin.NeedsReview);
        }
    }

    // Far off the predicted spot: no old placement, and the twin is flagged so it is not silently unplaced.
    [Fact]
    public async Task TwinFarFromThePredictedSpot_IsFlaggedInsteadOfPlaced()
    {
        var (h, _, twinId) = await PromoteWithWarpAsync(new HoldPositionNorm(0.70, 0.80));
        using (h)
        {
            await using var db = h.CreateContext();
            var twin = await db.Holds.SingleAsync(x => x.Id == twinId);
            Assert.Null(twin.FacetId);
            Assert.True(twin.NeedsReview);
        }
    }

    private static async Task<(WallTestHarness Harness, RowWall Wall, Guid TwinId)> PromoteWithWarpAsync(HoldPositionNorm warp)
    {
        var h = new WallTestHarness();
        var w = await SeedAsync(h);
        var s = await StageAsync(h, w.WallId, 3, 0);
        await using (var db = h.CreateContext())
        {
            var old = await db.Holds.SingleAsync(x => x.Id == w.CentreHoldId);
            (old.FacetId, old.PlaneAMm, old.PlaneBMm, old.MetricSource) = ("3", 120, 340, "texture-registration");
            var panel = await db.WallPanels.SingleAsync(p => p.Id == s[0].PanelId);
            panel.StagedPhoto = TestImages.Noise(2000, 1500);
            await db.SaveChangesAsync();
        }

        await Service(h).PromoteAsync(w.WallId, new BigUpdateConfirmation(
            [new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, s[0].HoldId)],
            [],
            [],
            [],
            new Dictionary<Guid, HoldPositionNorm> { [w.CentreHoldId] = warp }));
        return (h, w, s[0].HoldId);
    }
}
