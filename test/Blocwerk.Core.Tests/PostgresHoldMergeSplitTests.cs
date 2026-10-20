// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>The merge and split tools and their undo on PostgreSQL: the Restrict foreign keys, the filtered lineage index, the journal revert.</summary>
[Trait("Db", "Postgres")]
public class PostgresHoldMergeSplitTests
{
    [PostgresFact]
    public async Task AMergeAdoptsLineageAndLinksAndItsUndoRestoresThem()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldDuplicateScenario.CreateAsync(h);
        var kept = await s.AddHoldAsync(0.30, 0.30, 0.03, auto: true);
        var removed = await s.AddHoldAsync(0.33, 0.30, 0.03, auto: true);
        var twin = await s.AddHoldAsync(0.90, 0.80, 0.02, auto: true);
        var old = await HoldToolSeed.OldHoldAsync(h, 0.33, 0.30);
        await HoldToolSeed.LineageAsync(h, old, removed);
        await HoldToolSeed.PanelLinkAsync(h, removed, twin);
        var both = await s.AddBoulderAsync("Both", (kept, HoldType.Start), (removed, HoldType.Top));

        var merged = await s.MergeSplit().MergeAsync(h.WallId, kept, removed);

        Assert.DoesNotContain(removed, (await s.HoldsAsync()).Keys);
        Assert.Contains((old, kept), await HoldToolSeed.LineageRowsAsync(h));
        Assert.Contains(await HoldToolSeed.PanelLinksAsync(h), l => l.SetEquals([kept, twin]));
        Assert.Equal(HoldType.Top, (await s.LinksAsync(both)).Single().Type);

        var undo = await s.MergeSplit().UndoAsync(h.WallId, merged.BatchId);

        Assert.True(undo.Reverted, undo.Error);
        Assert.Contains(removed, (await s.HoldsAsync()).Keys);
        Assert.Equal(2, (await s.LinksAsync(both)).Count);
        Assert.Contains((old, removed), await HoldToolSeed.LineageRowsAsync(h));
        Assert.Contains(await HoldToolSeed.PanelLinksAsync(h), l => l.SetEquals([removed, twin]));
    }

    [PostgresFact]
    public async Task ASplitMovesTheChosenBoulderAndItsUndoRemovesTheNewPart()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldDuplicateScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.30, 0.30, 0.04, auto: true, shape: HoldToolSeed.Octagon(0.04));
        var goes = await s.AddBoulderAsync("Goes", (hold, HoldType.Top));

        var split = await s.MergeSplit().SplitAsync(h.WallId, hold, [new NormalizedPoint(0.31, 0.2), new NormalizedPoint(0.31, 0.4)], [goes]);

        Assert.Equal(split.OtherHoldId, (await s.LinksAsync(goes)).Single().HoldId);

        var undo = await s.MergeSplit().UndoAsync(h.WallId, split.BatchId);

        Assert.True(undo.Reverted, undo.Error);
        Assert.Equal([hold], (await s.HoldsAsync()).Keys);
        Assert.Equal(hold, (await s.LinksAsync(goes)).Single().HoldId);
    }
}
