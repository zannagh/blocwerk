// <copyright file="PanelRedetectMergeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Redetect;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Redetect;

/// <summary>
/// The wall editor's re-detect end to end with a stub detector: a hold that is found again keeps its identity and takes
/// the detected shape, the same hold recorded twice is merged (boulders move, undoable in the duplicate review), a
/// detection that is no stored hold becomes a new one, and a second run changes nothing.
/// </summary>
public class PanelRedetectMergeTests
{
    [Fact]
    public async Task AHoldFoundAgain_KeepsItsIdentity_AndTakesTheDetectedShape()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var jug = await s.AddHoldAsync(0.5, 0.5, 0.03, auto: true, color: "red", name: "Jug");
        var boulder = await s.AddBoulderAsync("Crimpy", (jug, HoldType.Start));
        RedetectRun.Detects(harness, new DetectedHold(0.508, 0.5, 0.028, "red", 0.9));

        var done = await s.Redetect().RunAsync(harness.WallId, s.PanelId);

        Assert.Equal((1, 0, 0), (done.BetterShapes, done.NewHolds, done.Removed));
        var hold = Assert.Single(await s.HoldsAsync()).Value;
        Assert.Equal((jug, "Jug", "red"), (hold.Id, hold.Name, hold.Color));
        Assert.Equal((0.508, 0.028), (hold.X, hold.Radius));
        Assert.Equal((jug, HoldType.Start), (await s.LinksAsync(boulder)).Select(l => (l.HoldId, l.Type)).Single());
    }

    [Fact]
    public async Task TheSameHoldRecordedTwice_IsMerged_BouldersMove_AndTheMergeCanBeUndone()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var used = await s.AddHoldAsync(0.3, 0.3, 0.03, auto: true, color: "blue", name: "Pinch");
        var spare = await s.AddHoldAsync(0.302, 0.3, 0.03, auto: true, color: "blue");
        var first = await s.AddBoulderAsync("First", (used, HoldType.Normal));
        var second = await s.AddBoulderAsync("Second", (used, HoldType.Normal));
        var third = await s.AddBoulderAsync("Third", (spare, HoldType.Top));
        RedetectRun.Detects(harness, new DetectedHold(0.31, 0.3, 0.03, "blue", 0.9));

        var plan = await s.Redetect().PlanAsync(harness.WallId, s.PanelId);
        var done = await s.Redetect().ApplyAsync(plan);

        var planned = Assert.Single(plan.Summary.Merges);
        Assert.Equal((used, spare, "Pinch", "blue", 1), (planned.KeptId, planned.RemovedId, planned.Name, planned.Color, planned.MovedBoulders));
        var merge = Assert.Single(done.Merges);
        Assert.Equal((1, 0), (merge.MovedBoulders, done.FailedMerges));
        var kept = Assert.Single(await s.HoldsAsync()).Value;
        Assert.Equal((used, 0.31), (kept.Id, kept.X));
        Assert.Equal((used, HoldType.Top), (await s.LinksAsync(third)).Select(l => (l.HoldId, l.Type)).Single());
        Assert.Equal(used, (await s.LinksAsync(first)).Single().HoldId);
        Assert.False(await s.NeedsReviewAsync(second));

        var recent = Assert.Single((await s.Service().ListAsync(harness.WallId, 0, 20)).RecentMerges);
        Assert.Equal(merge.BatchId, recent.BatchId);
        var reverted = await s.Service().RevertAsync(harness.WallId, recent.BatchId);
        Assert.True(reverted.Reverted, reverted.Error);
        Assert.Equal(new[] { used, spare }.Order(), (await s.HoldsAsync()).Keys.Order());
        Assert.Equal(spare, (await s.LinksAsync(third)).Single().HoldId);
    }

    [Fact]
    public async Task OverlappingHoldsOfDifferentColours_AndPairsThatAreOnlyLikely_AreNotMerged()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        await s.AddHoldAsync(0.3, 0.3, 0.03, auto: true, color: "blue");
        await s.AddHoldAsync(0.302, 0.3, 0.03, auto: true, color: "yellow");
        await s.AddHoldAsync(0.6, 0.6, 0.03, auto: true, color: "green");
        await s.AddHoldAsync(0.612, 0.6, 0.03, auto: true, color: "green");
        RedetectRun.Detects(harness, new DetectedHold(0.301, 0.3, 0.03, null, 0.9), new DetectedHold(0.606, 0.6, 0.03, "green", 0.9));

        var done = await s.Redetect().RunAsync(harness.WallId, s.PanelId);

        Assert.Empty(done.Merges);
        Assert.Equal((1, 0, 0), (done.ForReview, done.NewHolds, done.Removed));
        Assert.Equal(4, (await s.HoldsAsync()).Count);
        Assert.Empty((await s.Service().ListAsync(harness.WallId, 0, 20)).RecentMerges);
    }

    [Fact]
    public async Task ADetectionThatIsNoStoredHold_BecomesANewHold_AlsoOnAVolume()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var volume = await s.AddHoldAsync(0.5, 0.5, 0.1, auto: true, color: "white", name: "Volume");
        RedetectRun.Detects(
            harness,
            new DetectedHold(0.5, 0.5, 0.1, "white", 0.9),
            new DetectedHold(0.52, 0.5, 0.02, "green", 0.8),
            new DetectedHold(0.9, 0.2, 0.02, null, 0.7));

        var done = await s.Redetect().RunAsync(harness.WallId, s.PanelId);

        Assert.Equal((2, 0, 0), (done.NewHolds, done.BetterShapes, done.Merges.Count));
        var holds = await s.HoldsAsync();
        Assert.Equal((0.1, "Volume"), (holds[volume].Radius, holds[volume].Name));
        var onVolume = holds.Values.Single(h => h.Radius == 0.02 && h.X == 0.52);
        Assert.Equal(("green", "green", true, true, 1), (onVolume.Color, onVolume.ProposedColor, onVolume.IsAutoDetected, onVolume.NeedsReview, onVolume.Generation));
        Assert.Null(holds.Values.Single(h => h.X == 0.9).Color);
    }

    [Fact]
    public async Task HandMade_Virtual_Hidden_AndUsedHolds_Stay_AndASecondRunChangesNothing()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var drawn = await s.AddHoldAsync(0.2, 0.2, 0.03, auto: false, shape: Square(0.03));
        var dotted = await s.AddHoldAsync(0.4, 0.4, 0.03, auto: false, isVirtual: true);
        var hidden = await s.AddHoldAsync(0.6, 0.6, 0.03, auto: true);
        var usedButNotFound = await s.AddHoldAsync(0.8, 0.8, 0.03, auto: true);
        var notFound = await s.AddHoldAsync(0.8, 0.2, 0.03, auto: true);
        await s.AddBoulderAsync("Slab", (usedButNotFound, HoldType.Normal));
        await using (var db = harness.CreateContext())
        {
            (await db.Holds.SingleAsync(h => h.Id == hidden)).KeptUnconfirmedGeneration = 1;
            await db.SaveChangesAsync();
        }

        RedetectRun.Detects(
            harness,
            new DetectedHold(0.205, 0.2, 0.03, null, 0.9),
            new DetectedHold(0.405, 0.4, 0.03, null, 0.9),
            new DetectedHold(0.1, 0.9, 0.02, null, 0.9));
        var before = await s.HoldsAsync();

        var done = await s.Redetect().RunAsync(harness.WallId, s.PanelId);
        var after = await s.HoldsAsync();
        var again = await s.Redetect().PlanAsync(harness.WallId, s.PanelId);
        await s.Redetect().ApplyAsync(again);

        Assert.Equal((1, 1, 1, 1, 0), (done.NewHolds, done.Removed, done.HandShapesKept, done.Skipped, done.BetterShapes));
        Assert.DoesNotContain(notFound, after.Keys);
        foreach (var id in new[] { drawn, dotted, hidden, usedButNotFound })
        {
            Assert.Equal((before[id].X, before[id].Radius, before[id].IsVirtual, before[id].KeptUnconfirmedGeneration), (after[id].X, after[id].Radius, after[id].IsVirtual, after[id].KeptUnconfirmedGeneration));
            Assert.False(before[id].ShapeDiffers(after[id].ShapePoints));
        }

        Assert.False(again.Summary.Any);
        Assert.Equal(after.Keys.Order(), (await s.HoldsAsync()).Keys.Order());
    }

    [Fact]
    public async Task AModeratorMayReDetect_ButSurePairsAreOnlyLeftForTheAdminsReview()
    {
        using var harness = new WallTestHarness();
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        await s.AddHoldAsync(0.3, 0.3, 0.03, auto: true, color: "blue");
        await s.AddHoldAsync(0.302, 0.3, 0.03, auto: true, color: "blue");
        RedetectRun.Detects(harness, new DetectedHold(0.301, 0.3, 0.03, "blue", 0.9));
        harness.ActingUser = await harness.AddMemberAsync("moderator@example.org", WallRole.Moderator);

        var done = await s.Redetect().RunAsync(harness.WallId, s.PanelId);

        Assert.Empty(done.Merges);
        Assert.Equal(1, done.ForReview);
        Assert.Equal(2, (await s.HoldsAsync()).Count);
    }

    private static List<ShapePoint> Square(double half) =>
    [
        new() { Dx = -half, Dy = -half }, new() { Dx = half, Dy = -half }, new() { Dx = half, Dy = half }, new() { Dx = -half, Dy = half },
    ];
}
