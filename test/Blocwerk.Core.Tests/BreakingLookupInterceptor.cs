// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Makes the feed's boulder lookup fail on the server: its SQL is swapped for a query of a table that does not exist, so
/// Postgres raises an error that, without a savepoint, aborts the caller's transaction.
/// </summary>
internal sealed class BreakingLookupInterceptor : DbCommandInterceptor
{
    public int Broken { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Break(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Break(command);
        return ValueTask.FromResult(result);
    }

    private void Break(DbCommand command)
    {
        if (command.CommandText.Contains(FeedBoulderResolver.LookupTag, StringComparison.Ordinal))
        {
            command.CommandText = "SELECT * FROM bw_no_such_table";
            Broken++;
        }
    }
}
