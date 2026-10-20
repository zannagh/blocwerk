// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>The wall editor's split tool: the area rule, the new part, per-boulder choice, stacks, refusals and undo.</summary>
public sealed class HoldSplitToolTests : IDisposable
{
    /// <summary>A vertical cut a little right of the centre of a hold at (0.30, 0.30): the left part is the larger.</summary>
    private static readonly NormalizedPoint[] Cut = [new(0.31, 0.20), new(0.31, 0.40)];

    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task TheLargerPartKeepsTheIdLineageAndLinks_AndTheNewPartIsTheSameKindOfHold()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, color: "blue", name: "Pinch", shape: HoldToolSeed.Octagon(0.04));
        var twin = await s.AddHoldAsync(0.90, 0.80, 0.02, auto: true);
        var old = await HoldToolSeed.OldHoldAsync(harness, 0.30, 0.30);
        await HoldToolSeed.LineageAsync(harness, old, hold);
        await HoldToolSeed.PanelLinkAsync(harness, hold, twin);

        var result = await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, []);

        var holds = await s.HoldsAsync();
        var kept = holds[hold];
        var part = holds[result.OtherHoldId];
        Assert.Equal(hold, result.KeptHoldId);
        Assert.True(OutlineRings.Of(kept).Area > OutlineRings.Of(part).Area);
        Assert.True(kept.X < 0.31 && part.X > 0.31);
        Assert.Equal(("blue", HoldCategory.Hand, false, false, null), (part.Color, part.Category, part.NeedsReview, part.IsVirtual, part.Name));
        Assert.Equal((s.PanelId, 1), (part.WallPanelId!.Value, part.Generation));
        Assert.Equal("Pinch", kept.Name);
        Assert.Contains((old, hold), await HoldToolSeed.LineageRowsAsync(harness));
        Assert.Single(await HoldToolSeed.PanelLinksAsync(harness), l => l.SetEquals([hold, twin]));
    }

    [Fact]
    public async Task BouldersStayOnTheLargerPartUnlessChosenForTheOther()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var stays = await s.AddBoulderAsync("Stays", (hold, HoldType.Start));
        var goes = await s.AddBoulderAsync("Goes", (hold, HoldType.Top));

        var preview = await s.MergeSplit().PreviewSplitAsync(harness.WallId, hold, Cut);
        var result = await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, [goes]);

        Assert.Equal(["Goes", "Stays"], preview.Boulders.Select(b => b.Name));
        Assert.Equal(1, result.MovedBoulders);
        Assert.Equal((hold, HoldType.Start), (await s.LinksAsync(stays)).Select(l => (l.HoldId, l.Type)).Single());
        Assert.Equal((result.OtherHoldId, HoldType.Top), (await s.LinksAsync(goes)).Select(l => (l.HoldId, l.Type)).Single());
        Assert.False(await s.NeedsReviewAsync(goes));
    }

    [Fact]
    public async Task StackedHoldsGoToThePartThatHoldsTheirCentre()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var left = await s.AddHoldAsync(0.29, 0.30, 0.004, auto: true);
        var right = await s.AddHoldAsync(0.325, 0.30, 0.004, auto: true);
        await HoldToolSeed.StackAsync(harness, left, hold);
        await HoldToolSeed.StackAsync(harness, right, hold);

        var result = await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, []);

        var holds = await s.HoldsAsync();
        Assert.Equal(hold, holds[left].StackedOnHoldId);
        Assert.Equal(result.OtherHoldId, holds[right].StackedOnHoldId);
    }

    [Fact]
    public async Task CutsThatDoNotCrossOrShaveASliverAreRefused_InThePreviewAndOnSplit()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        NormalizedPoint[] halfway = [new(0.30, 0.20), new(0.30, 0.30)];
        NormalizedPoint[] sliver = [new(0.339, 0.20), new(0.339, 0.40)];
        var tools = s.MergeSplit();

        Assert.Contains("cross the whole hold", (await tools.PreviewSplitAsync(harness.WallId, hold, halfway)).Refusal);
        Assert.Contains("tiny", (await tools.PreviewSplitAsync(harness.WallId, hold, sliver)).Refusal);
        await Assert.ThrowsAsync<UserFacingException>(() => tools.SplitAsync(harness.WallId, hold, halfway, []));
        Assert.Single(await s.HoldsAsync());
    }

    [Fact]
    public async Task UndoRemovesTheNewPartAndRestoresTheShapeAndTheMemberships()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var goes = await s.AddBoulderAsync("Goes", (hold, HoldType.Top));
        var before = (await s.HoldsAsync())[hold];

        var result = await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, [goes]);
        var undo = await s.MergeSplit().UndoAsync(harness.WallId, result.BatchId);

        Assert.True(undo.Reverted, undo.Error);
        var holds = await s.HoldsAsync();
        Assert.Equal([hold], holds.Keys);
        Assert.Equal((before.X, before.Y, before.ShapePoints!.Count), (holds[hold].X, holds[hold].Y, holds[hold].ShapePoints!.Count));
        Assert.Equal(hold, (await s.LinksAsync(goes)).Single().HoldId);
    }

    [Fact]
    public async Task AVirtualHoldCannotBeSplit()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var placeholder = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: false, isVirtual: true);

        await Assert.ThrowsAsync<UserFacingException>(() => s.MergeSplit().SplitAsync(harness.WallId, placeholder, Cut, []));
    }
}
