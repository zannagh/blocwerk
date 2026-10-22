// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="WallMemberAccessTests"/> on PostgreSQL, so the gates are translated by the real provider.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresWallMemberAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task Members_AreListedOnlyToMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await WallMemberAccessChecks.MembersAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task SetterNames_FollowTheWall_DraftsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await WallMemberAccessChecks.SetterNamesAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task BoulderList_IsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await WallMemberAccessChecks.BoulderListAsync(h, viewer);
    }

    [PostgresFact]
    public async Task SetterNames_AreEmptyForAStrangerAndAWrongToken()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await WallMemberAccessChecks.SetterNamesForStrangersAsync(h);
    }

    [PostgresFact]
    public async Task KioskActingAsAMember_ReadsOnlyTheTabletsWall()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await WallMemberAccessChecks.KioskActAsStaysOnItsWallAsync(h);
    }
}
