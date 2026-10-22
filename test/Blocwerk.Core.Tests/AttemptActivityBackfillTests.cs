// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary><see cref="AttemptActivityBackfillChecks"/> on SQLite and on PostgreSQL.</summary>
public class AttemptActivityBackfillTests
{
    [Fact]
    public async Task TheStartupCleanup_LinksEntries_DeletesOrphans_AndIsIdempotent()
    {
        using var h = new WallTestHarness();
        await AttemptActivityBackfillChecks.LinksAndCleansAsync(h);
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task TheStartupCleanup_LinksEntries_DeletesOrphans_AndIsIdempotent_OnPostgres()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await AttemptActivityBackfillChecks.LinksAndCleansAsync(h);
    }
}
