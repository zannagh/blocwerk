// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The pg_stat_statements migration must be tolerant: on a server that does not preload the library (a dev or CI database)
/// it does nothing and does not fail; on one that does, it creates the extension. Either way it can run again.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresStatStatementsTests
{
    private const string MigrationId = "20261004150000_EnablePgStatStatements";

    [PostgresFact]
    public async Task TheMigration_CreatesTheExtensionOnlyWhenTheServerPreloadsIt_AndRunsAgain()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();

        await db.Database.MigrateAsync();
        var preloaded = await Preloaded(db);
        Assert.Equal(preloaded, await Installed(db));

        // Back to just before it, then forward again: the second run must not fail either.
        var migrator = db.GetService<IMigrator>();
        var previous = db.Database.GetMigrations().TakeWhile(m => m != MigrationId).Last();
        await migrator.MigrateAsync(previous);
        await db.Database.MigrateAsync();

        Assert.Equal(preloaded, await Installed(db));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task TheReport_WithoutTheExtension_SaysUnavailableInsteadOfFailing()
    {
        using var database = PostgresTestDatabase.Create();
        await using var db = database.CreateContext();
        if (await Installed(db))
        {
            return;
        }

        var factory = Substitute.For<IDbContextFactory<Data.BlocwerkDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(database.CreateContext()));
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(new User { Identifier = "admin", Role = IdentityRole.Admin });

        var report = await new DbStatsService(factory, users).GetReportAsync();

        Assert.False(report.Available);
        Assert.False(string.IsNullOrEmpty(report.Problem));
        Assert.Empty(report.ByTotalTime);
    }

    [PostgresFact]
    public async Task TheReport_IsForAdministratorsOnly()
    {
        using var database = PostgresTestDatabase.Create();
        var factory = Substitute.For<IDbContextFactory<Data.BlocwerkDbContext>>();
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(new User { Identifier = "user", Role = IdentityRole.User });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DbStatsService(factory, users).GetReportAsync());
    }

    private static async Task<bool> Preloaded(Data.BlocwerkDbContext db) =>
        (await db.Database.SqlQueryRaw<string>("SELECT current_setting('shared_preload_libraries') AS \"Value\"").SingleAsync())
            .Contains("pg_stat_statements", StringComparison.Ordinal);

    private static Task<bool> Installed(Data.BlocwerkDbContext db) =>
        db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') AS \"Value\"").SingleAsync();
}
