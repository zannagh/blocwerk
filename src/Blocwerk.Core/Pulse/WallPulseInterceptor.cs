// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Blocwerk.Core.Pulse;

/// <summary>
/// Pulses every tracked save of a job row (<see cref="WallPulseRows"/>), so the many places a pipeline writes its stage
/// need no call of their own. Read in <c>SavingChanges</c>, published once the write committed: after the save, or after
/// the commit of the explicit transaction it ran in; a rollback or a failed save drops them. Bulk writes
/// (<c>ExecuteUpdate</c>) bypass it and publish themselves (<c>GpuJobQueue</c>).
/// </summary>
public sealed class WallPulseInterceptor(IWallPulse pulse) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly IWallPulse channel = pulse;
    private readonly ConditionalWeakTable<DbContext, List<WallPulse>> pending = new();
    private readonly ConditionalWeakTable<DbContext, List<WallPulse>> awaitingCommit = new();

    /// <summary>
    /// Publishes through the interceptor on <paramref name="db"/>'s options: for a bulk write that has no pulse service
    /// of its own (a static helper). A context without the interceptor publishes nothing.
    /// </summary>
    public static void Publish(DbContext db, WallPulse pulse) =>
        db.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?.OfType<WallPulseInterceptor>().FirstOrDefault()
            ?.channel.Publish(pulse);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Forget(pending, eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(pending, eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget(awaitingCommit, eventData.Context);

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Forget(awaitingCommit, eventData.Context);
        return Task.CompletedTask;
    }

    private static void Forget(ConditionalWeakTable<DbContext, List<WallPulse>> table, DbContext? context)
    {
        if (context is not null)
        {
            table.Remove(context);
        }
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var pulses = WallPulseRows.Read(context.ChangeTracker);
        if (pulses.Count > 0)
        {
            pending.AddOrUpdate(context, pulses);
        }
    }

    private void Saved(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var pulses))
        {
            return;
        }

        pending.Remove(context);
        if (context.Database.CurrentTransaction is not null)
        {
            awaitingCommit.GetValue(context, _ => []).AddRange(pulses);
            return;
        }

        PublishAll(pulses);
    }

    private void Committed(DbContext? context)
    {
        if (context is not null && awaitingCommit.TryGetValue(context, out var pulses))
        {
            awaitingCommit.Remove(context);
            PublishAll(pulses);
        }
    }

    // The last pulse of each job in the batch: the throttle would collapse the others anyway.
    private void PublishAll(List<WallPulse> pulses)
    {
        foreach (var last in pulses.GroupBy(p => (p.WallId, p.Topic, p.JobId)).Select(g => g.Last()))
        {
            channel.Publish(last);
        }
    }
}
