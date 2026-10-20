// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>The wall editor's merge tool: identity, lineage, links, boulders, stacks, outline, refusals and undo.</summary>
public sealed class HoldMergeToolTests : IDisposable
{
    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task TheKeptHoldKeepsItsLineageAndLinks_AndAdoptsTheRemovedHolds()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.03, auto: true);
        var removed = await s.AddHoldAsync(0.33, 0.30, 0.03, auto: true);
        var twinOfKept = await s.AddHoldAsync(0.80, 0.80, 0.02, auto: true);
        var twinOfRemoved = await s.AddHoldAsync(0.90, 0.80, 0.02, auto: true);
        var oldOfKept = await HoldToolSeed.OldHoldAsync(harness, 0.30, 0.30);
        var oldOfRemoved = await HoldToolSeed.OldHoldAsync(harness, 0.33, 0.30);
        await HoldToolSeed.LineageAsync(harness, oldOfKept, kept);
        await HoldToolSeed.LineageAsync(harness, oldOfRemoved, removed);
        await HoldToolSeed.PanelLinkAsync(harness, kept, twinOfKept);
        await HoldToolSeed.PanelLinkAsync(harness, twinOfRemoved, removed);

        await s.MergeSplit().MergeAsync(harness.WallId, kept, removed);

        Assert.DoesNotContain(removed, (await s.HoldsAsync()).Keys);
        var lineage = await HoldToolSeed.LineageRowsAsync(harness);
        Assert.Contains((oldOfKept, kept), lineage);
        Assert.Contains((oldOfRemoved, kept), lineage);
        Assert.DoesNotContain(lineage, l => l.Old is null || l.New is null);
        var links = await HoldToolSeed.PanelLinksAsync(harness);
        Assert.Contains(links, l => l.SetEquals([kept, twinOfKept]));
        Assert.Contains(links, l => l.SetEquals([kept, twinOfRemoved]));
    }

    [Fact]
    public async Task BouldersMoveOver_TheStrongerRoleWins_AndABoulderOnBothIsFlagged()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.03, auto: true);
        var removed = await s.AddHoldAsync(0.33, 0.30, 0.03, auto: true, color: "red");
        var onlyRemoved = await s.AddBoulderAsync("OnlyRemoved", (removed, HoldType.Top));
        var both = await s.AddBoulderAsync("Both", (kept, HoldType.Start), (removed, HoldType.Top));

        var result = await s.MergeSplit().MergeAsync(harness.WallId, kept, removed);

        Assert.Equal((1, 1), (result.MovedBoulders, result.FlaggedForReview));
        Assert.Equal((kept, HoldType.Top), (await s.LinksAsync(onlyRemoved)).Select(l => (l.HoldId, l.Type)).Single());
        Assert.Equal(HoldType.Top, (await s.LinksAsync(both)).Single().Type);
        Assert.True(await s.NeedsReviewAsync(both));
        Assert.False(await s.NeedsReviewAsync(onlyRemoved));
        Assert.Equal("red", (await s.HoldsAsync())[kept].Color);
    }

    [Fact]
    public async Task WhatSatOnTheRemovedHoldNowSitsOnTheKeptOne()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.03, auto: true);
        var removed = await s.AddHoldAsync(0.33, 0.30, 0.03, auto: true);
        var bolt = await s.AddHoldAsync(0.34, 0.30, 0.005, auto: true);
        await HoldToolSeed.StackAsync(harness, bolt, removed);

        await s.MergeSplit().MergeAsync(harness.WallId, kept, removed);

        Assert.Equal(kept, (await s.HoldsAsync())[bolt].StackedOnHoldId);
    }

    [Fact]
    public async Task TheOutlineIsTheUnion_ACircleGapIsBridged_AndTheShapeIsAPlainPolygon()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.02, auto: true, shape: HoldToolSeed.Octagon(0.02));
        var removed = await s.AddHoldAsync(0.345, 0.30, 0.02, auto: true);
        await using (var db = harness.CreateContext())
        {
            db.Holds.Single(h => h.Id == kept).ShapeControlPoints = HoldToolSeed.Octagon(0.02);
            await db.SaveChangesAsync();
        }

        var preview = await s.MergeSplit().PreviewMergeAsync(harness.WallId, kept, removed);
        await s.MergeSplit().MergeAsync(harness.WallId, kept, removed);

        Assert.True(preview.Bridged);
        var merged = (await s.HoldsAsync())[kept];
        Assert.Null(merged.ShapeControlPoints);
        Assert.Equal(HoldOutlineSource.Manual, merged.OutlineSource);
        var rings = OutlineRings.Of(merged);
        Assert.True(rings.Covers(new P2(0.30, 0.30)) && rings.Covers(new P2(0.345, 0.30)) && rings.Covers(new P2(0.3225, 0.30)));
        Assert.InRange(merged.X, 0.31, 0.335);
    }

    [Fact]
    public async Task APocketAwayFromTheOtherHoldStaysAHole()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var removed = await s.AddHoldAsync(0.35, 0.30, 0.02, auto: true);
        await using (var db = harness.CreateContext())
        {
            db.Holds.Single(h => h.Id == kept).ShapeHoles = [HoldToolSeed.Octagon(0.008).Select(p => new Entities.ShapePoint { Dx = p.Dx - 0.015, Dy = p.Dy }).ToList()];
            await db.SaveChangesAsync();
        }

        await s.MergeSplit().MergeAsync(harness.WallId, kept, removed);

        var rings = OutlineRings.Of((await s.HoldsAsync())[kept]);
        Assert.Single(rings.Holes);
        Assert.False(rings.Covers(new P2(0.285, 0.30)));
    }

    [Fact]
    public async Task UndoRestoresBothHoldsTheirLinksAndTheMemberships_AndTheBatchIsTheWalls()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.03, auto: true);
        var removed = await s.AddHoldAsync(0.33, 0.30, 0.03, auto: true);
        var twin = await s.AddHoldAsync(0.90, 0.80, 0.02, auto: true);
        var old = await HoldToolSeed.OldHoldAsync(harness, 0.33, 0.30);
        await HoldToolSeed.LineageAsync(harness, old, removed);
        await HoldToolSeed.PanelLinkAsync(harness, removed, twin);
        var only = await s.AddBoulderAsync("Only", (removed, HoldType.Normal));
        var before = (await s.HoldsAsync())[kept];

        var result = await s.MergeSplit().MergeAsync(harness.WallId, kept, removed);
        await using (var db = harness.CreateContext())
        {
            var batches = await ChangeJournalBatchSelection.ForWallAsync(db, harness.WallId, false, null, null, default);
            Assert.Contains(batches, b => b.Id == result.BatchId && b.Label == HoldMergeSplitService.MergeBatchLabel);
        }

        var undo = await s.MergeSplit().UndoAsync(harness.WallId, result.BatchId);

        Assert.True(undo.Reverted, undo.Error);
        var holds = await s.HoldsAsync();
        Assert.Contains(removed, holds.Keys);
        Assert.Equal((before.X, before.Radius, before.ShapePoints), (holds[kept].X, holds[kept].Radius, holds[kept].ShapePoints));
        Assert.Equal(removed, (await s.LinksAsync(only)).Single().HoldId);
        Assert.Contains((old, removed), await HoldToolSeed.LineageRowsAsync(harness));
        Assert.Contains(await HoldToolSeed.PanelLinksAsync(harness), l => l.SetEquals([removed, twin]));
    }

    [Fact]
    public async Task ThePreviewSuggestsKeepingTheHoldWithMoreBoulders_ThenTheOneWithLineage()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var a = await s.AddHoldAsync(0.30, 0.30, 0.03, auto: true);
        var b = await s.AddHoldAsync(0.33, 0.30, 0.03, auto: true);
        var c = await s.AddHoldAsync(0.60, 0.30, 0.03, auto: true);
        var d = await s.AddHoldAsync(0.63, 0.30, 0.03, auto: true);
        await s.AddBoulderAsync("OnB", (b, HoldType.Normal));
        await HoldToolSeed.LineageAsync(harness, await HoldToolSeed.OldHoldAsync(harness, 0.63, 0.30), d);

        var byBoulders = await s.MergeSplit().PreviewMergeAsync(harness.WallId, a, b);
        var byLineage = await s.MergeSplit().PreviewMergeAsync(harness.WallId, c, d);

        Assert.Equal(b, byBoulders.SuggestedKeptId);
        Assert.Equal(1, byBoulders.Boulders[b]);
        Assert.Equal(d, byLineage.SuggestedKeptId);
    }

    [Fact]
    public async Task MergesThatMakeNoSenseAreRefused()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var a = await s.AddHoldAsync(0.30, 0.30, 0.02, auto: true);
        var far = await s.AddHoldAsync(0.80, 0.80, 0.02, auto: true);
        var placeholder = await s.AddHoldAsync(0.32, 0.30, 0.02, auto: false, isVirtual: true);
        var old = await HoldToolSeed.OldHoldAsync(harness, 0.31, 0.30, s.PanelId);
        var tools = s.MergeSplit();

        await Assert.ThrowsAsync<UserFacingException>(() => tools.MergeAsync(harness.WallId, a, a));
        Assert.Contains("too far apart", (await Assert.ThrowsAsync<UserFacingException>(() => tools.MergeAsync(harness.WallId, a, far))).Message);
        Assert.Contains("Virtual", (await Assert.ThrowsAsync<UserFacingException>(() => tools.MergeAsync(harness.WallId, a, placeholder))).Message);
        Assert.Contains("earlier photo", (await Assert.ThrowsAsync<UserFacingException>(() => tools.MergeAsync(harness.WallId, a, old))).Message);
        Assert.Equal(4, (await s.HoldsAsync()).Count);
    }
}
