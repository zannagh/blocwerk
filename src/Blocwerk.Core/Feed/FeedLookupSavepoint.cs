// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Runs the feed's lookup inside the caller's open transaction without risking it. A statement that fails on Postgres
/// aborts the whole transaction (25P02), so the user's save would fail after all. Inside a transaction the lookup runs
/// under a savepoint and a failure rolls back to it, which leaves the transaction usable. Without a transaction a failed
/// lookup touches nothing.
/// </summary>
internal static class FeedLookupSavepoint
{
    private const string Name = "bw_feed_lookup";

    public static T Run<T>(DbContext context, Func<T> lookup)
    {
        var transaction = context.Database.CurrentTransaction;
        if (transaction is not { SupportsSavepoints: true })
        {
            return lookup();
        }

        transaction.CreateSavepoint(Name);
        try
        {
            var result = lookup();
            transaction.ReleaseSavepoint(Name);
            return result;
        }
        catch
        {
            transaction.RollbackToSavepoint(Name);
            throw;
        }
    }

    public static async Task<T> RunAsync<T>(DbContext context, Func<Task<T>> lookup, CancellationToken ct)
    {
        var transaction = context.Database.CurrentTransaction;
        if (transaction is not { SupportsSavepoints: true })
        {
            return await lookup();
        }

        await transaction.CreateSavepointAsync(Name, ct);
        try
        {
            var result = await lookup();
            await transaction.ReleaseSavepointAsync(Name, ct);
            return result;
        }
        catch
        {
            await transaction.RollbackToSavepointAsync(Name, CancellationToken.None);
            throw;
        }
    }
}
