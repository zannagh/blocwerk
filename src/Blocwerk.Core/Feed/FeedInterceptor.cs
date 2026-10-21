// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using System.Runtime.CompilerServices;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Writes the live-update feed from <c>SaveChanges</c> (design §5.3). <c>SavingChanges</c> maps the tracked changes
/// (<see cref="FeedCollector"/>) and adds the rows to the SAME context, as <see cref="Data.ChangeJournalInterceptor"/> does,
/// so they are part of the save: inside the caller's transaction when there is one, otherwise inside the transaction EF
/// opens for a save of more than one statement. Rows and edit commit or roll back together.
/// <para>
/// After the commit the walls are woken (<see cref="IFeedSignal"/>): straight after a save without an explicit
/// transaction, on <c>TransactionCommitted</c> with one, never after a rollback (the same rule as
/// <see cref="Data.DomainChangeInterceptor"/>). A failed or cancelled save drops its rows and wakes (E5), and detaches the
/// rows so a retried save collects them afresh instead of writing them twice. A suppressed save writes and wakes nothing.
/// Held wakes belong to one transaction: its commit releases them, and anything else that ends it drops them. A singleton: per-context state lives in
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>s.
/// </para>
/// </summary>
public sealed class FeedInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly FeedMapperRegistry registry;
    private readonly IFeedSignal signal;
    private readonly ILogger<FeedInterceptor> logger;
    private readonly ConditionalWeakTable<DbContext, List<WallChange>> pending = new();
    private readonly ConditionalWeakTable<DbContext, FeedPendingCommit> awaitingCommit = new();

    public FeedInterceptor(FeedMapperRegistry registry, IFeedSignal signal, ILogger<FeedInterceptor> logger)
    {
        this.registry = registry;
        this.signal = signal;
        this.logger = logger;
    }

    // An earlier interceptor that suppressed the save (HasResult) means nothing is written: nothing to collect.
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (!result.HasResult)
        {
            Collect(eventData.Context);
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!result.HasResult && eventData.Context is { } context && !registry.IsEmpty)
        {
            Track(context, await FeedCollector.CollectAsync(context, registry, logger, cancellationToken));
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context)
        {
            // With async false the compaction runs synchronously and the task is already complete.
            FeedCommentCompaction.ApplyAsync(context, logger, async: false, CancellationToken.None).GetAwaiter().GetResult();
        }

        Saved(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await FeedCommentCompaction.ApplyAsync(context, logger, async: true, cancellationToken);
        }

        Saved(eventData.Context);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Drop(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Drop(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Drop(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        WakeCommitted(eventData.Context, transaction);

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        WakeCommitted(eventData.Context, transaction);
        return Task.CompletedTask;
    }

    // EF has no "transaction disposed" event. A transaction disposed without commit or rollback has rolled back, and the
    // next one the context starts or adopts (or a failure) is where that shows: its held wakes are dropped there, and a
    // commit only ever releases the wakes of its own transaction.
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        DropUncommitted(eventData.Context);
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        DropUncommitted(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        DropUncommitted(eventData.Context);
        return result;
    }

    public ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        DropUncommitted(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => DropUncommitted(eventData.Context);

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        DropUncommitted(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => DropUncommitted(eventData.Context);

    public Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        DropUncommitted(eventData.Context);
        return Task.CompletedTask;
    }

    private void Collect(DbContext? context)
    {
        // Nothing is mapped (a test context without mappers): the save pays one check and no change-tracker walk.
        if (context is null || registry.IsEmpty)
        {
            return;
        }

        Track(context, FeedCollector.Collect(context, registry, logger));
    }

    private void Track(DbContext context, IReadOnlyList<WallChange> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        // Added to, not replaced: a save another interceptor runs on this context before ours has seen the outer save's
        // SavedChanges must not drop the outer save's wakes.
        context.AddRange(rows);
        pending.GetValue(context, _ => []).AddRange(rows);
    }

    private void Saved(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var rows))
        {
            return;
        }

        pending.Remove(context);

        // EF has read the database-generated Seq and TxId back. A row whose Seq is still temporary was never written (a
        // later interceptor suppressed the save): it wakes nothing, and leaving it tracked would let the next save insert
        // it beside the rows that save collects afresh. The written rows are immutable from here, so the context stops
        // tracking them too: a long transaction of many saves (a replay) would otherwise drag them through every
        // DetectChanges.
        var written = rows.Where(r => !context.Entry(r).Property(c => c.Seq).IsTemporary).ToList();
        var wakes = Wakes(written);
        Detach(context, rows);
        if (context.Database.CurrentTransaction?.GetDbTransaction() is { } transaction)
        {
            var held = awaitingCommit.GetValue(context, _ => new FeedPendingCommit(transaction));
            if (!ReferenceEquals(held.Transaction, transaction))
            {
                awaitingCommit.Remove(context); // left over from a transaction that ended unseen
                held = awaitingCommit.GetValue(context, _ => new FeedPendingCommit(transaction));
            }

            held.Wakes.AddRange(wakes);
            return;
        }

        WakeAll(wakes);
    }

    private void Drop(DbContext? context)
    {
        if (context is not null)
        {
            FeedCommentCompaction.Drop(context);
        }

        if (context is not null && pending.TryGetValue(context, out var rows))
        {
            pending.Remove(context);
            Detach(context, rows);
        }
    }

    private void WakeCommitted(DbContext? context, DbTransaction transaction)
    {
        if (context is not null && awaitingCommit.TryGetValue(context, out var held))
        {
            awaitingCommit.Remove(context);
            if (ReferenceEquals(held.Transaction, transaction))
            {
                WakeAll(held.Wakes);
            }
        }
    }

    private void DropUncommitted(DbContext? context)
    {
        if (context is not null)
        {
            awaitingCommit.Remove(context);
        }
    }

    // One wake per wall, with the newest transaction id: the hub re-polls until that one is below the horizon.
    private void WakeAll(IEnumerable<FeedWake> wakes)
    {
        foreach (var wake in wakes.GroupBy(w => w.WallId).Select(g => g.MaxBy(w => w.TxId ?? 0)))
        {
            signal.Wake(wake);
        }
    }

    private static List<FeedWake> Wakes(IEnumerable<WallChange> rows) =>
        rows.Where(r => r.WallId is not null).Select(r => new FeedWake(r.WallId!.Value, r.TxId)).ToList();

    private static void Detach(DbContext context, IEnumerable<WallChange> rows)
    {
        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }
}
