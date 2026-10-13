// <copyright file="HoldStackLifecycleTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.HoldStacks;

/// <summary>What happens to "sits on" when holds are carried into a new generation, deleted, merged, re-stacked or backfilled.</summary>
public class HoldStackLifecycleTests
{
    private static readonly (double X, double Y) Spot = (0.5, 0.5);
    private static readonly (double X, double Y) OnIt = (0.53, 0.5);

    [Fact]
    public async Task AWallUpdate_KeepsTheStack_OnTheSuccessors_WhetherCopiedOrFoundAgain()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var old = await s.AddPanelAsync(0);
        var volume = await s.AddHoldAsync(old, Spot, configure: h => h.Radius = 0.12);
        var small = await s.AddHoldAsync(old, OnIt, configure: h => (h.Radius, h.StackedOnHoldId) = (0.02, volume));
        var loose = await s.AddHoldAsync(old, (0.9, 0.9), configure: h => (h.Radius, h.StackedOnHoldId) = (0.02, Guid.NewGuid()));
        var staged = await s.AddPanelAsync(0, generation: 1, staged: true);
        var twin = await s.AddHoldAsync(staged, Spot, generation: 1, h => h.Radius = 0.12);

        // The volume was found again (its detection carries it on); the small hold and the loose one are copied.
        await s.BigUpdate().PromoteAsync(s.WallId, new BigUpdateConfirmation(
            [new CarryoverDecision(volume, CarryKind.Carried, twin), new CarryoverDecision(small, CarryKind.Carried, null), new CarryoverDecision(loose, CarryKind.Carried, null)],
            [],
            [],
            []));

        await using var db = s.Harness.CreateContext();
        var live = await db.Holds.AsNoTracking().Where(h => h.WallPanelId == staged).ToListAsync();
        Assert.Equal(3, live.Count);
        Assert.Equal(twin, live.Single(h => Math.Abs(h.X - OnIt.X) < 1e-6).StackedOnHoldId);
        Assert.Null(live.Single(h => h.X > 0.8).StackedOnHoldId);

        // The old generation is history and stays as it was.
        Assert.Equal(volume, (await s.GetAsync(small)).StackedOnHoldId);
    }

    [Fact]
    public async Task DeletingTheBase_LeavesItsHoldsOnTheWall()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var volume = await s.AddHoldAsync(panel, Spot, configure: h => h.Radius = 0.12);
        var small = await s.AddHoldAsync(panel, OnIt, configure: h => (h.Radius, h.StackedOnHoldId) = (0.02, volume));

        await s.Harness.WallService.DeleteHoldAsync(volume);

        var left = await s.GetAsync(small);
        Assert.Null(left.StackedOnHoldId);
        Assert.Equal(OnIt.X, left.X);
    }

    [Fact]
    public async Task MergingTwoHolds_PutsWhatSatOnEitherOntoTheKeptOne()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.5, 0.5, 0.12, auto: true);
        var removed = await s.AddHoldAsync(0.505, 0.5, 0.12, auto: true);
        var onKept = await s.AddHoldAsync(0.47, 0.5, 0.02, auto: true);
        var onRemoved = await s.AddHoldAsync(0.54, 0.5, 0.02, auto: true);
        await StackAsync(harness, (onKept, kept), (onRemoved, removed));

        await s.Service().MergeAsync(harness.WallId, kept, removed, HoldMergeMode.KeepLeft);

        var holds = await s.HoldsAsync();
        Assert.False(holds.ContainsKey(removed));
        Assert.Equal((kept, kept), (holds[onKept].StackedOnHoldId, holds[onRemoved].StackedOnHoldId));
    }

    [Fact]
    public async Task MergingAHoldIntoWhatSatOnIt_NeverLeavesAHoldOnItself()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var wall = await s.AddHoldAsync(0.5, 0.5, 0.2, auto: true);
        var lower = await s.AddHoldAsync(0.5, 0.5, 0.12, auto: true);
        var upper = await s.AddHoldAsync(0.5, 0.5, 0.1, auto: true);
        var top = await s.AddHoldAsync(0.5, 0.5, 0.02, auto: true);
        await StackAsync(harness, (lower, wall), (upper, lower), (top, lower));

        // The upper hold stays and the hold it sat on goes: it takes that hold's base and its other holds.
        await s.Service().MergeAsync(harness.WallId, upper, lower, HoldMergeMode.KeepLeft);

        var holds = await s.HoldsAsync();
        Assert.Equal((wall, upper), (holds[upper].StackedOnHoldId, holds[top].StackedOnHoldId));
    }

    [Fact]
    public async Task APersonPutsAHoldOnAnother_OrBackOnTheWall_AndALoopOrAnotherPhotoIsRefused()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var (panel, otherPanel) = (await s.AddPanelAsync(0), await s.AddPanelAsync(1));
        var volume = await s.AddHoldAsync(panel, Spot, configure: h => h.Radius = 0.12);
        var small = await s.AddHoldAsync(panel, (0.9, 0.9), configure: h => h.Radius = 0.02);
        var elsewhere = await s.AddHoldAsync(otherPanel, Spot);
        Task Put(Guid hold, FieldUpdate<Guid?> on) =>
            s.Harness.WallService.UpdateHoldAsync(hold, new HoldEdit { X = 0.5, Y = 0.5, Radius = 0.05, StackedOn = on });

        // Not inside the volume at all: a person may still say it sits on it.
        await Put(small, volume);
        Assert.Equal(volume, (await s.GetAsync(small)).StackedOnHoldId);

        await Assert.ThrowsAsync<UserFacingException>(() => Put(volume, small));
        await Assert.ThrowsAsync<UserFacingException>(() => Put(volume, volume));
        await Assert.ThrowsAsync<UserFacingException>(() => Put(small, elsewhere));
        Assert.Equal((volume, null), ((await s.GetAsync(small)).StackedOnHoldId, (await s.GetAsync(volume)).StackedOnHoldId));

        // An edit that says nothing about it leaves it alone; null takes the hold off.
        await Put(small, FieldUpdate<Guid?>.Keep);
        Assert.Equal(volume, (await s.GetAsync(small)).StackedOnHoldId);
        await Put(small, FieldUpdate<Guid?>.Set(null));
        Assert.Null((await s.GetAsync(small)).StackedOnHoldId);
    }

    [Fact]
    public async Task TheStartupBackfill_StacksTheLiveHoldsOnce_AndNeverAgainWhatAPersonTookApart()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var volume = await s.AddHoldAsync(panel, Spot, configure: h => h.Radius = 0.12);
        var small = await s.AddHoldAsync(panel, OnIt, configure: h => h.Radius = 0.02);
        var hidden = await s.AddHoldAsync(panel, (0.47, 0.5), configure: h => (h.Radius, h.KeptUnconfirmedGeneration) = (0.02, 0));
        var apart = await s.AddHoldAsync(panel, (0.9, 0.9), configure: h => h.Radius = 0.02);

        await HoldStackBackfill.RunIfNeededAsync(s.Harness.DbContextFactory, null, NullLogger.Instance);

        Assert.Equal(volume, (await s.GetAsync(small)).StackedOnHoldId);
        Assert.Null((await s.GetAsync(hidden)).StackedOnHoldId);
        Assert.Null((await s.GetAsync(apart)).StackedOnHoldId);
        Assert.Null((await s.GetAsync(volume)).StackedOnHoldId);
        await using (var db = s.Harness.CreateContext())
        {
            Assert.NotNull((await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == s.WallId)).HoldStacksBackfilledAt);
            Assert.Equal((0, 4), await HoldStackBackfill.BackfillWallAsync(db, s.WallId, CancellationToken.None));
        }

        // Somebody takes the hold off the volume: the next start leaves it off.
        await s.SetAsync(small, h => h.StackedOnHoldId = null);
        await HoldStackBackfill.RunIfNeededAsync(s.Harness.DbContextFactory, null, NullLogger.Instance);

        Assert.Null((await s.GetAsync(small)).StackedOnHoldId);
    }

    private static async Task StackAsync(WallTestHarness harness, params (Guid Upper, Guid Lower)[] pairs)
    {
        await using var db = harness.CreateContext();
        foreach (var (upper, lower) in pairs)
        {
            (await db.Holds.FirstAsync(h => h.Id == upper)).StackedOnHoldId = lower;
        }

        await db.SaveChangesAsync();
    }
}
