// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The epoch remembers which database it was started in (oid, and the cluster's system identifier when readable). Any
/// restore or copy, live dump or not, lands in a database with another identity and starts a new epoch.
/// </summary>
[Trait("Db", "Postgres")]
[Collection(PostgresFeedCollection.Name)]
public sealed class PostgresFeedIdentityTests
{
    [PostgresFact]
    public async Task Initialize_RecordsTheDatabaseOid_AndTheSystemIdentifier()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);

        await kit.Feed.InitializeAsync();

        await using var db = pg.CreateContext();
        var epoch = await db.FeedEpochs.SingleAsync();
        var oid = await db.Database.SqlQueryRaw<long>(
            "SELECT oid::bigint AS \"Value\" FROM pg_database WHERE datname = current_database()").SingleAsync();
        Assert.Equal(oid, epoch.DatabaseOid);
        Assert.NotNull(epoch.SystemIdentifier);
    }

    [PostgresTheory]
    [InlineData("\"DatabaseOid\" = \"DatabaseOid\" + 1")]
    [InlineData("\"SystemIdentifier\" = \"SystemIdentifier\" + 1")]
    [InlineData("\"DatabaseOid\" = NULL")]
    public async Task AnotherStoredIdentity_TruncatesTheFeed_StartsANewEpoch_AndOldCursorsResync(string change)
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var oldEpoch = await kit.Feed.InitializeAsync();
        await kit.CommitAsync(kit.Row("before-restore"));
        var oldCursor = await kit.Feed.CurrentAsync();
        await ExecuteAsync(pg, $"UPDATE \"FeedEpochs\" SET {change}");

        var restarted = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);
        var newEpoch = await restarted.InitializeAsync();
        var page = await restarted.ReadAsync(kit.Filter, oldCursor, 100);

        Assert.NotEqual(oldEpoch, newEpoch);
        Assert.True(page.Resync);
        await using var db = pg.CreateContext();
        Assert.Equal(0, await db.WallChanges.CountAsync());
    }

    [PostgresFact]
    public async Task UnknownSystemIdentifier_WithTheSameOid_KeepsTheEpoch()
    {
        using var pg = PostgresTestDatabase.Create();
        var kit = new PostgresFeedTestKit(pg);
        var epoch = await kit.Feed.InitializeAsync();
        await ExecuteAsync(pg, "UPDATE \"FeedEpochs\" SET \"SystemIdentifier\" = NULL");

        var restarted = new WallFeed(() => pg.CreateContext(), NullLogger<WallFeed>.Instance);

        Assert.Equal(epoch, await restarted.InitializeAsync());
    }

    [PostgresFact]
    public async Task RoleThatMayNotCallPgControlSystem_FallsBackToTheOidAlone()
    {
        using var pg = PostgresTestDatabase.Create();
        var role = "blocwerk_test_role_" + Guid.NewGuid().ToString("N")[..12];

        // Function grants live in the database's own catalog, so the revoke stays inside this test database.
        await ExecuteAsync(pg, $"CREATE ROLE {role} LOGIN PASSWORD 'feed'");
        try
        {
            var grants = $"REVOKE EXECUTE ON FUNCTION pg_control_system() FROM PUBLIC; "
                + $"GRANT ALL ON ALL TABLES IN SCHEMA public TO {role}; GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO {role}";
            await ExecuteAsync(pg, grants);
            var asRole = new NpgsqlConnectionStringBuilder(pg.ConnectionString) { Username = role, Password = "feed" }.ConnectionString;
            var feed = new WallFeed(() => TestDb.Create(asRole), NullLogger<WallFeed>.Instance);

            var epoch = await feed.InitializeAsync();
            var again = await new WallFeed(() => TestDb.Create(asRole), NullLogger<WallFeed>.Instance).InitializeAsync();

            await using var db = pg.CreateContext();
            var stored = await db.FeedEpochs.SingleAsync();
            Assert.Null(stored.SystemIdentifier);
            Assert.NotNull(stored.DatabaseOid);
            Assert.Equal(epoch, again);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(pg, $"REASSIGN OWNED BY {role} TO CURRENT_USER; DROP OWNED BY {role}; DROP ROLE {role}");
        }
    }

    private static async Task ExecuteAsync(PostgresTestDatabase pg, string sql)
    {
        await using var db = pg.CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}
