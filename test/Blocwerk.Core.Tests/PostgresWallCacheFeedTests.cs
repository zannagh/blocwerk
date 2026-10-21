// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The circuit cache on Postgres, where cursors carry real transaction ids: a hold event evicts only its generation and the
/// aggregate, and a load whose <c>CursorAtLoad</c> is at or after an event keeps its entry.
/// </summary>
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresWallCacheFeedTests
{
    [PostgresFact]
    public async Task AHoldEvent_EvictsItsGenerationAndTheAggregate()
    {
        await using var f = new CircuitCacheFixture(PostgresTestDatabase.Create());
        await f.StartAsync();
        await f.WarmAsync();

        await f.DeliverAsync(f.Row(FeedKinds.HoldChanged, f.Holds[0].Id, new HoldPayload(0, null)));

        Assert.False(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Equal([(f.WallId, 1)], f.Store.HoldSets.Keys);
    }

    [PostgresFact]
    public async Task AnEventTheLoadAlreadyReflects_EvictsNothing()
    {
        await using var f = new CircuitCacheFixture(PostgresTestDatabase.Create());
        await f.StartAsync();
        await f.WarmAsync();
        var row = f.Row(FeedKinds.HoldChanged, f.Holds[0].Id, new HoldPayload(0, null));
        await f.CommitAsync(false, row);
        f.Store.Walls.Evict(f.WallId);
        f.Store.HoldSets.Evict((f.WallId, 0));
        await f.WarmAsync();

        f.Signal.Wake(new Core.Feed.FeedWake(f.WallId, row.TxId));
        await CircuitCacheFixture.WaitUntilAsync(() => f.Deliveries.Count == 1, seconds: 20);

        Assert.True(f.Store.Walls.TryPeek(f.WallId, out _));
        Assert.Equal(2, f.Store.HoldSets.Keys.Count());
        Assert.Empty(f.Changes);
    }
}
