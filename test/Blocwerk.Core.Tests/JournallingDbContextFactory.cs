// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Like <see cref="TestDbContextFactory"/> over the same database, but with the production
/// <see cref="ChangeJournalInterceptor"/> wired to <paramref name="journal"/>, so a service's named batches can be observed.
/// </summary>
/// <param name="connectionString">The database (e.g. <see cref="TestDbContextFactory.ConnectionString"/>).</param>
/// <param name="journal">The ambient journal the service opens its batches on.</param>
public sealed class JournallingDbContextFactory(string connectionString, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
{
    public BlocwerkDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ChangeJournalInterceptor(journal))
            .Options;
        return new SqliteBlocwerkDbContext(options);
    }
}
