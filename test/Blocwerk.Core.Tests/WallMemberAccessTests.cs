// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// Wall-keyed reads over unfiltered tables: the member list, the setter names and the member boulder list used to answer
/// ANY caller for ANY wall id. SQLite; see <see cref="PostgresWallMemberAccessTests"/>.
/// </summary>
public class WallMemberAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task Members_AreListedOnlyToMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await WallMemberAccessChecks.MembersAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task SetterNames_FollowTheWall_DraftsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await WallMemberAccessChecks.SetterNamesAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task BoulderList_IsForMembersOnly(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await WallMemberAccessChecks.BoulderListAsync(h, viewer);
    }

    [Fact]
    public async Task SetterNames_AreEmptyForAStrangerAndAWrongToken()
    {
        using var h = new WallTestHarness();
        await WallMemberAccessChecks.SetterNamesForStrangersAsync(h);
    }

    [Fact]
    public async Task KioskActingAsAMember_ReadsOnlyTheTabletsWall()
    {
        using var h = new WallTestHarness();
        await WallMemberAccessChecks.KioskActAsStaysOnItsWallAsync(h);
    }
}
