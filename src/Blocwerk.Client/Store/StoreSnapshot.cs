// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>What client-store.js says the client may show now (its <c>snapshot()</c>).</summary>
/// <param name="State">The state, as client-store.js names it (<c>ready</c>, <c>locked</c>, …).</param>
/// <param name="Partition">The ledger's active label it read, or null.</param>
/// <param name="ShareWall">The wall the page's share link names, or null.</param>
/// <param name="Walls">The active partition's walls the page may read; empty unless ready.</param>
/// <param name="Mine">The viewer's marks of those walls; empty unless the label is <c>u-</c>.</param>
public sealed record StoreSnapshot(
    string State,
    string? Partition,
    Guid? ShareWall,
    IReadOnlyList<StoreWallRow> Walls,
    IReadOnlyList<StoreMineRow> Mine)
{
    /// <summary>The snapshot of a device that shows nothing.</summary>
    /// <param name="state">The state to report.</param>
    /// <returns>A snapshot with no partition, walls or marks.</returns>
    public static StoreSnapshot Nothing(string state) => new(state, null, null, [], []);
}
