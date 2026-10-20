// <copyright file="PostgresLookOverNewHoldTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>"Use the new hold" asked per boulder, on a real PostgreSQL (the new column, the batch, the undo).</summary>
[Trait("Db", "Postgres")]
public class PostgresLookOverNewHoldTests
{
    [PostgresFact]
    public async Task AllYes()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await NewHoldPerBoulderScenarios.AllYesAsync(h);
    }

    [PostgresFact]
    public async Task Mixed()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await NewHoldPerBoulderScenarios.MixedAsync(h);
    }

    [PostgresFact]
    public async Task AllNo()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await NewHoldPerBoulderScenarios.AllNoAsync(h);
    }

    [PostgresFact]
    public async Task UndoingAMix_PutsEverythingBackExactly()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await NewHoldPerBoulderScenarios.UndoAsync(h, mixed: true);
    }

    [PostgresFact]
    public async Task UndoingAllYes_PutsTheMarksBackToo()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await NewHoldPerBoulderScenarios.UndoAsync(h, mixed: false);
    }
}
