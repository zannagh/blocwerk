// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The real migration chain on a real PostgreSQL: it applies to an empty database, the model matches the schema it builds,
/// and the migrations that carry raw SQL (<c>now()</c>, boolean and null handling) do what they say to rows of their day.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresMigrationTests
{
    [PostgresFact]
    public async Task TheMigrationChain_AppliesToAnEmptyDatabase_AndLeavesNoPendingModelChanges()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact]
    public async Task TheMigrationChain_CanBeRunOnAgain_WithNothingToDo()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task ClearWriteOnInactiveApiKeys_ClearsRevokedAndExpiredKeys_AndLeavesLiveOnesAlone()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await MigrateBeforeAsync(database, nameof(ClearWriteOnInactiveApiKeys));
        var user = await PostgresRows.InsertAsync(database.ConnectionString, "Users", ("Identifier", "u@test"), ("DisplayName", "U"));
        var now = DateTimeOffset.UtcNow;
        var revoked = await InsertKeyAsync(database, user, allowWrite: true, revokedAt: now.AddDays(-1));
        var expired = await InsertKeyAsync(database, user, allowWrite: true, expiresAt: now.AddMinutes(-1));
        var live = await InsertKeyAsync(database, user, allowWrite: true, expiresAt: now.AddDays(30));
        var noExpiry = await InsertKeyAsync(database, user, allowWrite: true);
        var readOnly = await InsertKeyAsync(database, user, allowWrite: false);

        await MigrateToAsync(database, nameof(ClearWriteOnInactiveApiKeys));

        var written = (await PostgresRows.QueryAsync(
            database.ConnectionString, "SELECT \"Id\", \"AllowWrite\" FROM \"ApiKeys\"", r => (Id: r.GetGuid(0), Write: r.GetBoolean(1))))
            .ToDictionary(r => r.Id, r => r.Write);
        Assert.Equal(
            (false, false, true, true, false),
            (written[revoked], written[expired], written[live], written[noExpiry], written[readOnly]));
    }

    [PostgresFact]
    public async Task TheRetiredAtBackfill_StampsEveryInactiveModelOnce_AndNoActiveOne()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await MigrateBeforeAsync(database, nameof(AddGeometryModelRetention));
        var user = await PostgresRows.InsertAsync(database.ConnectionString, "Users", ("Identifier", "u@test"), ("DisplayName", "U"));
        var wall = await PostgresRows.InsertAsync(database.ConnectionString, "Walls", ("Name", "W"), ("OwnerId", user));
        var active = await InsertModelAsync(database, wall, isActive: true);
        var inactive = await InsertModelAsync(database, wall, isActive: false);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        await MigrateToAsync(database, nameof(AddGeometryModelRetention));

        var retired = (await PostgresRows.QueryAsync(
            database.ConnectionString,
            "SELECT \"Id\", \"RetiredAt\" FROM \"WallGeometryModels\"",
            r => (Id: r.GetGuid(0), At: r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1))))
            .ToDictionary(r => r.Id, r => r.At);
        Assert.Null(retired[active]);
        Assert.True(retired[inactive] >= before && retired[inactive] <= DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [PostgresFact]
    public async Task TheRetiredAtBackfill_KeepsAnExistingRetirementTime_WhenRunAgain()
    {
        using var database = PostgresTestDatabase.CreateEmpty();
        await MigrateToAsync(database, null);
        await using var db = database.CreateContext();
        var user = await PostgresRows.InsertAsync(database.ConnectionString, "Users", ("Identifier", "u@test"), ("DisplayName", "U"));
        var wall = await PostgresRows.InsertAsync(database.ConnectionString, "Walls", ("Name", "W"), ("OwnerId", user));
        var retiredAt = DateTimeOffset.UtcNow.AddDays(-9);
        var model = await InsertModelAsync(database, wall, isActive: false, retiredAt);

        await db.Database.ExecuteSqlRawAsync(AddGeometryModelRetention.BackfillRetiredAtSql);

        var stored = await PostgresRows.QueryAsync(
            database.ConnectionString,
            $"SELECT \"RetiredAt\" FROM \"WallGeometryModels\" WHERE \"Id\" = '{model}'",
            r => r.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(retiredAt.ToUnixTimeMilliseconds(), Assert.Single(stored).ToUnixTimeMilliseconds());
    }

    private static Task<Guid> InsertKeyAsync(
        PostgresTestDatabase database, Guid user, bool allowWrite, DateTimeOffset? revokedAt = null, DateTimeOffset? expiresAt = null) =>
        PostgresRows.InsertAsync(
            database.ConnectionString,
            "ApiKeys",
            ("UserId", user),
            ("AllowWrite", allowWrite),
            ("RevokedAt", revokedAt),
            ("ExpiresAt", expiresAt));

    private static Task<Guid> InsertModelAsync(PostgresTestDatabase database, Guid wall, bool isActive, DateTimeOffset? retiredAt = null)
    {
        var values = new List<(string, object?)> { ("WallId", wall), ("IsActive", isActive), ("Json", "{}"), ("Source", "test") };
        if (retiredAt is not null)
        {
            values.Add(("RetiredAt", retiredAt));
        }

        return PostgresRows.InsertAsync(database.ConnectionString, "WallGeometryModels", [.. values]);
    }

    /// <summary>Migrates up to the migration just before <paramref name="name"/>, so rows can be written at the schema it meets.</summary>
    private static async Task MigrateBeforeAsync(PostgresTestDatabase database, string name)
    {
        await using var db = database.CreateContext();
        var migrations = db.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_" + name, StringComparison.Ordinal));
        Assert.True(index > 0, $"No migration named {name}.");
        await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
    }

    /// <summary>Migrates up to and including <paramref name="name"/>, or all the way when null.</summary>
    private static async Task MigrateToAsync(PostgresTestDatabase database, string? name)
    {
        await using var db = database.CreateContext();
        var target = name is null ? null : db.Database.GetMigrations().Single(m => m.EndsWith("_" + name, StringComparison.Ordinal));
        await db.GetService<IMigrator>().MigrateAsync(target);
    }
}
