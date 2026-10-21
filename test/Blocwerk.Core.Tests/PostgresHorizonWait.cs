// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Waits for the Postgres feed horizon to pass a transaction. The horizon is <c>pg_snapshot_xmin</c>, which is
/// cluster-wide: any transaction holding an xid anywhere on the server (another test class in its own database, a
/// background task) holds it back, however long ago this test's own rows committed. A test that asserts a load or a
/// cursor already covers its rows waits here first, on the condition rather than on time.
/// </summary>
internal static class PostgresHorizonWait
{
    /// <summary>Polls <see cref="PostgresFeedHorizon.CeilingAsync"/> until its TxId is at least <paramref name="txId"/>.</summary>
    public static async Task UntilCeilingReachesAsync(Func<BlocwerkDbContext> context, long txId, double seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            long? ceiling;
            await using (var db = context())
            {
                ceiling = (await PostgresFeedHorizon.Instance.CeilingAsync(db, CancellationToken.None)).TxId;
            }

            if (ceiling >= txId)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The feed horizon stayed at {ceiling}, below {txId}: a transaction elsewhere on the server is still open.");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Waits until every transaction that has started so far (this one's xid included) has ended.</summary>
    public static async Task UntilNowIsPastAsync(Func<BlocwerkDbContext> context, double seconds = 30)
    {
        long current;
        await using (var db = context())
        {
            current = await db.Database.SqlQueryRaw<long>("SELECT pg_current_xact_id()::text::bigint AS \"Value\"").SingleAsync();
        }

        await UntilCeilingReachesAsync(context, current, seconds);
    }
}
