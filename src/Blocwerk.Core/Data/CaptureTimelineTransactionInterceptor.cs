// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Data;

/// <summary>
/// The second half of <see cref="CaptureTimelineInterceptor"/>: timeline changes saved inside a caller's transaction are
/// merged once it committed (by a context of their own, since the caller's context still holds the finished transaction),
/// and dropped when it rolled back or failed. A transaction disposed without either leaves its changes held only until the
/// context starts its next transaction (held changes belong to one transaction id) or goes away.
/// </summary>
public sealed class CaptureTimelineTransactionInterceptor : DbTransactionInterceptor
{
    private CaptureTimelineTransactionInterceptor()
    {
    }

    /// <summary>The one instance (stateless).</summary>
    public static CaptureTimelineTransactionInterceptor Instance { get; } = new();

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            CaptureTimelineMerge.MergeAllAsync(context, CaptureTimelineMerge.TakeDeferred(context, eventData.TransactionId), sync: true, fresh: true).GetAwaiter().GetResult();
        }

        base.TransactionCommitted(transaction, eventData);
    }

    public override async Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await CaptureTimelineMerge.MergeAllAsync(
                context, CaptureTimelineMerge.TakeDeferred(context, eventData.TransactionId), sync: false, fresh: true);
        }

        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        // Held changes of an earlier transaction that ended without a commit or rollback EF saw (disposed) go now.
        CaptureTimelineMerge.DropStale(eventData.Context, eventData.TransactionId);
        return base.TransactionStarted(connection, eventData, result);
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        CaptureTimelineMerge.DropStale(eventData.Context, eventData.TransactionId);
        return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        CaptureTimelineMerge.TakeDeferred(eventData.Context);
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        CaptureTimelineMerge.TakeDeferred(eventData.Context);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        CaptureTimelineMerge.TakeDeferred(eventData.Context);
        base.TransactionFailed(transaction, eventData);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        CaptureTimelineMerge.TakeDeferred(eventData.Context);
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }
}
