// <copyright file="HoldEditFollowIn3DTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Blocwerk.Core.Tests.FollowIn3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3D model follows 2D hold edits in the refinement queue: a moved or added hold is placed again from its panel
/// photo's registration (cached per photo and model), its panel fields never change, the placements go into one rolling
/// edit run that reverts cleanly next to a wall-wide run, proposals it covers are hidden (never changed), and a wall
/// without a 3D model behaves exactly as before. Photo c0 of <see cref="HoldPlacementScenario"/>: a hold at normalised
/// (x, y) left of the middle belongs at facet "0", a = 4000·x, b = 3000 − 3000·y. Races: <see cref="HoldEditFollowIn3DGuardTests"/>.
/// </summary>
public class HoldEditFollowIn3DTests
{
    [Fact]
    public async Task MovedHold_IsRegisteredAgainFromItsPhoto_AndItsPanelFieldsStay()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var ids = await SeedPlacedAsync(s);
        var moved = ids[2];
        await Walls(h, s.Queue).UpdateHoldAsync(moved, Edit(0.3, 0.45));
        var others = await s.LoadHoldsAsync();
        var edited = others[moved];
        var opened = s.Matcher.Opened;

        await Queue(h, s.Service()).RunAsync(h.WallId, [moved], default);

        var holds = await s.LoadHoldsAsync();
        var hold = holds[moved];
        Assert.Equal(("0", HoldMetric.TextureRegistration), (hold.FacetId, hold.MetricSource));
        Assert.Equal(1200, hold.PlaneAMm!.Value, 1);
        Assert.Equal(1650, hold.PlaneBMm!.Value, 1);
        Assert.Equal((edited.WallPanelId, edited.X, edited.Y, edited.Radius), (hold.WallPanelId, hold.X, hold.Y, hold.Radius));
        Assert.False(hold.ShapeDiffers(edited.ShapePoints));
        Assert.All(ids.Where(id => id != moved), id => Assert.Equal(
            (others[id].FacetId, others[id].PlaneAMm, others[id].PlaneBMm), (holds[id].FacetId, holds[id].PlaneAMm, holds[id].PlaneBMm)));

        // The run's registration of the photo was reused; the edit run is not shown as the latest run.
        Assert.Equal(opened, s.Matcher.Opened);
        Assert.NotEqual(HoldPlacementTrigger.Edit, (await s.Service().GetStatusAsync(h.WallId)).LatestRun!.Trigger);
    }

    [Fact]
    public async Task EditPlacements_ShareOneRollingRun_ThatRevertsNextToTheWallWideRun()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var ids = await SeedPlacedAsync(s);
        var (walls, queue) = (Walls(h, s.Queue), Queue(h, s.Service()));
        await walls.UpdateHoldAsync(ids[2], Edit(0.3, 0.45));
        await queue.RunAsync(h.WallId, [ids[2]], default);
        await walls.UpdateHoldAsync(ids[4], Edit(0.25, 0.3));
        await queue.RunAsync(h.WallId, [ids[4]], default);
        await walls.UpdateHoldAsync(ids[2], Edit(0.3, 0.55));
        var estimate = (await s.LoadHoldsAsync())[ids[2]];
        await queue.RunAsync(h.WallId, [ids[2]], default);

        var runs = await RunsAsync(h);
        var edit = Assert.Single(runs, r => r.Trigger == HoldPlacementTrigger.Edit);
        Assert.Equal(new[] { ids[2], ids[4] }.Order(), HoldPlacementEntry.FromJson(edit.HoldsJson).Select(e => e.HoldId).Order());
        Assert.Equal(1350, (await s.LoadHoldsAsync())[ids[2]].PlaneBMm!.Value, 1);

        // The wall-wide run leaves the edit-placed holds alone; the rolling run restores the edit's own estimate.
        var service = s.Service();
        await service.RevertAsync(h.WallId, runs.Single(r => r.Trigger != HoldPlacementTrigger.Edit).Id);
        Assert.Equal(HoldMetric.TextureRegistration, (await s.LoadHoldsAsync())[ids[2]].MetricSource);
        await service.RevertAsync(h.WallId, edit.Id);
        var reverted = (await s.LoadHoldsAsync())[ids[2]];
        Assert.Equal((estimate.FacetId, estimate.PlaneAMm, estimate.PlaneBMm, estimate.MetricSource), (reverted.FacetId, reverted.PlaneAMm, reverted.PlaneBMm, reverted.MetricSource));
        Assert.Equal((0.3, 0.55), (reverted.X, reverted.Y));
    }

    [Fact]
    public async Task AddedHold_IsPlacedIn3D_AfterTheQueue_RegisteringThePhotoOnce()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var walls = Walls(h, s.Queue);
        var queue = Queue(h, s.Service());

        var first = await walls.AddHoldAsync(h.WallId, 0.3, 0.45, 0.01, null, wallPanelId: s.PanelC0);
        s.Queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(x => x.Single() == first.Id));
        await queue.RunAsync(h.WallId, [first.Id], default);
        var second = await walls.AddHoldAsync(h.WallId, 0.2, 0.2, 0.01, null, wallPanelId: s.PanelC0);
        await queue.RunAsync(h.WallId, [second.Id], default);

        var holds = await s.LoadHoldsAsync();
        Assert.Equal(("0", HoldMetric.TextureRegistration), (holds[first.Id].FacetId, holds[first.Id].MetricSource));
        Assert.Equal(1200, holds[first.Id].PlaneAMm!.Value, 1);
        Assert.Equal(800, holds[second.Id].PlaneAMm!.Value, 1);
        Assert.Equal(2400, holds[second.Id].PlaneBMm!.Value, 1);
        Assert.Equal(1, s.Matcher.Opened);
    }

    [Fact]
    public async Task ProposalAtAnAddedHold_IsHiddenButStaysPending_AndShowsAgainWhenTheHoldMoves()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var (near, far) = (await ProposalAsync(s, 1210, 1650), await ProposalAsync(s, 500, 500));
        var proposals = Proposals(h);
        Assert.Equal(2, (await proposals.ListAsync(h.WallId)).Count);
        var walls = Walls(h, s.Queue);

        var added = await walls.AddHoldAsync(h.WallId, 0.3, 0.45, 0.01, null, wallPanelId: s.PanelC0);
        await Queue(h, s.Service()).RunAsync(h.WallId, [added.Id], default);

        Assert.Equal(far, Assert.Single(await proposals.ListAsync(h.WallId)).Id);
        await using (var db = h.CreateContext())
        {
            Assert.Equal(HoldProposalStatus.Pending, (await db.HoldProposals.AsNoTracking().SingleAsync(p => p.Id == near)).Status);
        }

        await walls.UpdateHoldAsync(added.Id, Edit(0.1, 0.1));
        await Queue(h, s.Service()).RunAsync(h.WallId, [added.Id], default);
        Assert.Equal(2, (await proposals.ListAsync(h.WallId)).Count);
    }

    [Fact]
    public async Task WallWithoutA3DModel_IsUnaffected()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync()).IsActive = false;
            await db.SaveChangesAsync();
        }

        var proposal = await ProposalAsync(s, 1210, 1650);
        var added = await Walls(h, s.Queue).AddHoldAsync(h.WallId, 0.3, 0.45, 0.01, null, wallPanelId: s.PanelC0);
        await Queue(h, s.Service()).RunAsync(h.WallId, [added.Id], default);

        var hold = (await s.LoadHoldsAsync())[added.Id];
        Assert.Null(hold.FacetId);
        Assert.Null(hold.PlaneAMm);
        Assert.Null(hold.MetricSource);
        Assert.Equal((0.3, 0.45, 0.01), (hold.X, hold.Y, hold.Radius));
        Assert.Equal(0, s.Matcher.Opened);
        Assert.Equal(proposal, Assert.Single(await Proposals(h).ListAsync(h.WallId)).Id);
        Assert.Empty(await RunsAsync(h));
    }

    [Fact]
    public async Task MovedHoldOnAVolume_IsPlacedOnItAgain_OthersKeepTheirs()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        await s.Service().DetectAsync(h.WallId);
        var bare = await s.PlacementAsync(s.Bare);
        await using (var db = h.CreateContext())
        {
            var hold = await db.Holds.SingleAsync(x => x.Id == s.OnVolume);
            (hold.PlaneAMm, hold.X) = (hold.PlaneAMm + 20, (hold.PlaneAMm!.Value + 20) / 3000);
            await db.SaveChangesAsync();
        }

        await Queue(h, null).RunAsync(h.WallId, [s.OnVolume], default);

        var placement = await s.PlacementAsync(s.OnVolume);
        Assert.NotNull(placement);
        Assert.Equal(1350, placement.FromA, 1);
        Assert.Equal(bare?.ToJson(), (await s.PlacementAsync(s.Bare))?.ToJson());
        Assert.Equal(1, (await s.VolumeAsync()).HoldCount);
    }

    [Fact]
    public void EditEstimate_IsEligibleAfterAnEdit_ButNotForAWholeWallRun()
    {
        var hold = new Hold { WallPanelId = Guid.NewGuid(), FacetId = "0", PlaneAMm = 1, PlaneBMm = 2, MetricSource = HoldMetric.HoldFit };

        Assert.True(HoldTexturePlacer.IsEligibleAfterEdit(hold));
        Assert.False(HoldTexturePlacer.IsEligible(hold));
        hold.MetricSource = HoldMetric.MultiMarker;
        Assert.False(HoldTexturePlacer.IsEligibleAfterEdit(hold));
    }
}
