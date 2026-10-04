// <copyright file="MidWriteEditDbContextFactory.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Data.Common;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Hands out contexts over the harness database that run a concurrent edit once, in the middle of a wall-wide placement run's
/// write: after the run row is saved, at whichever comes first of the next transaction start (a write that claims its rows
/// first) or the next save of modified holds (a write that does not). The edit goes through its own context, as a user's
/// request would. With <c>afterClaim</c> it starts only at that save, i.e. after a claiming write locked its rows.
/// </summary>
public sealed class MidWriteEditDbContextFactory : IDbContextFactory<BlocwerkDbContext>, ISaveChangesInterceptor, IDbTransactionInterceptor
{
    private readonly string connectionString;
    private readonly Func<Task> edit;
    private readonly bool afterClaim;
    private bool armed;

    public MidWriteEditDbContextFactory(string connectionString, Func<Task> edit, bool afterClaim = false)
    {
        this.connectionString = connectionString;
        this.edit = edit;
        this.afterClaim = afterClaim;
    }

    /// <summary>Gets a value indicating whether the edit ran.</summary>
    public bool Edited { get; private set; }

    public BlocwerkDbContext CreateDbContext() => TestDb.Create(connectionString, this);

    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var tracker = eventData.Context!.ChangeTracker;
        if (tracker.Entries<HoldPlacementRun>().Any(e => e.State == EntityState.Added))
        {
            armed = true;
        }
        else if (tracker.Entries<Hold>().Any(e => e.State == EntityState.Modified))
        {
            await EditOnceAsync();
        }

        return result;
    }

    public async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
    {
        if (!afterClaim)
        {
            await EditOnceAsync();
        }

        return result;
    }

    private async Task EditOnceAsync()
    {
        if (!armed || Edited)
        {
            return;
        }

        Edited = true;
        await edit();
    }
}
