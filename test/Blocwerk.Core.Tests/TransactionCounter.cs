// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>Counts the transactions EF starts on a context.</summary>
internal sealed class TransactionCounter : DbTransactionInterceptor
{
    private int started;

    public int Started => started;

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Interlocked.Increment(ref started);
        return base.TransactionStarted(connection, eventData, result);
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref started);
        return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
    }
}
