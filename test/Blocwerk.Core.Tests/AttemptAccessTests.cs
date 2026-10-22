// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// Who may log, list and delete ticks. The boulder used to be looked up by id alone, so any signed-in user could tick
/// ANY boulder in the installation and read who ticked it. SQLite; see <see cref="PostgresAttemptAccessTests"/>.
/// </summary>
public class AttemptAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task Log_IsForMembersOnly_WithNoExistenceOracle(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await AttemptAccessChecks.LogAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task List_IsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await AttemptAccessChecks.ListAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task Delete_IsForTheAuthor_OnAWallTheyCanSee(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await AttemptAccessChecks.DeleteAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task IsMember_MatchesTheWriteGate(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await AttemptAccessChecks.IsMemberAsync(h, viewer);
    }

    [Fact]
    public async Task KioskActingAsAMember_TicksOnlyOnTheTabletsWall()
    {
        using var h = new WallTestHarness();
        await AttemptAccessChecks.KioskActAsStaysOnItsWallAsync(h);
    }
}
