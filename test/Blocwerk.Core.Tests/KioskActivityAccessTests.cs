// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// The kiosk tablet's read-only view of its wall's activity and comments (<see cref="KioskActivityAccessChecks"/>). SQLite;
/// see <see cref="PostgresKioskActivityAccessTests"/>.
/// </summary>
public class KioskActivityAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task WallActivity_TheRestingKioskReadsThePublicPart_MembersAll_ShareLinksNone(DraftViewer viewer)
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
}
