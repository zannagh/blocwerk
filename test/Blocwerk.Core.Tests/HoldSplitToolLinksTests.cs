// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>The split's panel links (one part takes them) and its lineage (the new part gets none, by design).</summary>
public sealed class HoldSplitToolLinksTests : IDisposable
{
    private static readonly NormalizedPoint[] Cut = [new(0.31, 0.20), new(0.31, 0.40)];

    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task ThePanelLinksGoWithThePartMostBouldersUse_UnlessTheEditorChoseOtherwise()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var (hold, twin) = await LinkedHoldAsync(s);
        var a = await s.AddBoulderAsync("A", (hold, HoldType.Normal));
        var b = await s.AddBoulderAsync("B", (hold, HoldType.Normal));

        var split = await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, [a, b]);

        Assert.Single(await HoldToolSeed.PanelLinksAsync(harness), l => l.SetEquals([split.OtherHoldId, twin]));
    }

    [Fact]
    public async Task AnExplicitChoiceKeepsTheLinksOnTheLargerPart()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var (hold, twin) = await LinkedHoldAsync(s);
        var a = await s.AddBoulderAsync("A", (hold, HoldType.Normal));

        await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, [a], panelLinksToSmaller: false);

        Assert.Single(await HoldToolSeed.PanelLinksAsync(harness), l => l.SetEquals([hold, twin]));
    }

    [Fact]
    public async Task ThePreviewCountsTheLinks_AndTheNewPartGetsNoLineageRow()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var (hold, _) = await LinkedHoldAsync(s);
        var old = await HoldToolSeed.OldHoldAsync(harness, 0.30, 0.30);
        await HoldToolSeed.LineageAsync(harness, old, hold);

        var preview = await s.MergeSplit().PreviewSplitAsync(harness.WallId, hold, Cut);
        var split = await s.MergeSplit().SplitAsync(harness.WallId, hold, Cut, []);

        Assert.Equal(1, preview.PanelLinks);
        Assert.Equal([(old, (Guid?)hold)], await HoldToolSeed.LineageRowsAsync(harness));
        Assert.DoesNotContain(await HoldToolSeed.LineageRowsAsync(harness), l => l.New == split.OtherHoldId);
    }

    private async Task<(Guid Hold, Guid Twin)> LinkedHoldAsync(HoldDuplicateScenario s)
    {
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var twin = await s.AddHoldAsync(0.90, 0.80, 0.02, auto: true);
        await HoldToolSeed.PanelLinkAsync(harness, hold, twin);
        return (hold, twin);
    }
}
