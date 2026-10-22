// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// The kiosk tablet's read-only view of its boulders' activity and comments (<see cref="KioskActivityAccessChecks"/>,
/// <see cref="KioskBylineChecks"/>). SQLite;
/// see <see cref="PostgresKioskActivityAccessTests"/>.
/// </summary>
public class KioskActivityAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task WallActivity_IsForMembersOnly_NeverTheRestingKioskOrAShareLink(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await KioskActivityAccessChecks.WallActivityAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task BoulderActivity_FollowsTheBoulder_NoDraftsOutside_NoArchivedOnTheTablet(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await KioskActivityAccessChecks.BoulderActivityAsync(h, viewer);
    }

    [Fact]
    public async Task TheRestingKiosk_NeverReadsAnotherWallsActivity()
    {
        using var h = new WallTestHarness();
        await KioskActivityAccessChecks.KioskStaysOnItsWallAsync(h);
    }

    [Fact]
    public async Task TheRestingKiosk_ReadsNoArchivedBouldersComments()
    {
        using var h = new WallTestHarness();
        await KioskActivityAccessChecks.ArchivedCommentsAsync(h);
    }

    [Theory]
    [InlineData(DraftViewer.KioskAnonymous)]
    [InlineData(DraftViewer.KioskActAsMember)]
    [InlineData(DraftViewer.Member)]
    [InlineData(DraftViewer.ShareAnonymous)]
    public async Task Bylines_TheRestingKioskNamesConsentingMembersAndSetters_ElseAMember(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await KioskBylineChecks.BylinesAsync(h, viewer);
    }
}
