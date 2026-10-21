// <copyright file="PostgresWallEnhancementTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The wall enhancement on PostgreSQL: the migration's tables and unique index, the journal batches, undo and resume.</summary>
[Trait("Db", "Postgres")]
public class PostgresWallEnhancementTests
{
    [PostgresFact]
    public async Task FullCycle_ReviewFinalize_AndNeverTwicePerGeneration()
    {
        using var f = await WallEnhancementFixture.CreateAsync(PostgresTestDatabase.Create());
        await WallEnhancementScenarios.FullCycleAsync(f);
    }

    [PostgresFact]
    public async Task TheUniqueIndex_RefusesASecondRunOfTheSameGeneration()
    {
        using var f = await WallEnhancementFixture.CreateAsync(PostgresTestDatabase.Create());
        await WallEnhancementScenarios.UniqueIndexAsync(f);
    }

    [PostgresFact]
    public async Task TwoStopsSalvagingAtOnce_ListAHoldOnce()
    {
        using var f = await WallEnhancementFixture.CreateAsync(PostgresTestDatabase.Create());
        await WallEnhancementLateWriteTests.RaceAsync(f);
    }

    [PostgresFact]
    public async Task ARunCutOffByARestart_ResumesWithTheInterruptedStep()
    {
        using var f = await WallEnhancementFixture.CreateAsync(PostgresTestDatabase.Create());
        await WallEnhancementScenarios.ResumeAsync(f);
    }
}
