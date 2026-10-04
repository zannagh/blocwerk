// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>A merge and its undo on PostgreSQL: the composite membership key, the Restrict foreign keys and the journal revert.</summary>
[Trait("Db", "Postgres")]
public class PostgresHoldDuplicateTests
{
    [PostgresFact]
    public async Task AMergeMovesTheMembershipsAndItsUndoRestoresThem()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldDuplicateScenario.CreateAsync(h);
        var kept = await s.AddHoldAsync(0.3, 0.3, 0.05, auto: false);
        var removed = await s.AddHoldAsync(0.3, 0.3, 0.02, auto: true);
        var both = await s.AddBoulderAsync("Both", (kept, HoldType.Start), (removed, HoldType.Top));
        var only = await s.AddBoulderAsync("Only", (removed, HoldType.Normal));

        var merged = await s.Service().MergeAsync(h.WallId, kept, removed, HoldMergeMode.KeepLeft);

        Assert.DoesNotContain(removed, (await s.HoldsAsync()).Keys);
        Assert.Equal(HoldType.Top, (await s.LinksAsync(both)).Single().Type);
        Assert.Equal(kept, (await s.LinksAsync(only)).Single().HoldId);

        var reverted = await s.Service().RevertAsync(h.WallId, merged.BatchId);

        Assert.True(reverted.Reverted, reverted.Error);
        Assert.Contains(removed, (await s.HoldsAsync()).Keys);
        Assert.Equal(2, (await s.LinksAsync(both)).Count);
        Assert.Equal(removed, (await s.LinksAsync(only)).Single().HoldId);
    }
}
