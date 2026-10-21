// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>Cancels the request's token the moment EF commits its transaction: a client that went away just after the save.</summary>
internal sealed class CancelOnCommit : DbTransactionInterceptor
{
    private readonly CancellationTokenSource cts;

    public CancelOnCommit(CancellationTokenSource cts)
    {
        this.cts = cts;
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => cts.Cancel();

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        cts.Cancel();
        return Task.CompletedTask;
    }
}
