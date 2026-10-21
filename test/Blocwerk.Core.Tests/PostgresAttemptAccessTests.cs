// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="AttemptAccessTests"/> on PostgreSQL, so the gates are translated by the real provider.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresAttemptAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task Log_IsForMembersOnly_WithNoExistenceOracle(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await AttemptAccessChecks.LogAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task List_IsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await AttemptAccessChecks.ListAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task Delete_IsForTheAuthor_OnAWallTheyCanSee(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await AttemptAccessChecks.DeleteAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task IsMember_MatchesTheWriteGate(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await AttemptAccessChecks.IsMemberAsync(h, viewer);
    }

    [PostgresFact]
    public async Task KioskActingAsAMember_TicksOnlyOnTheTabletsWall()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await AttemptAccessChecks.KioskActAsStaysOnItsWallAsync(h);
    }
}
