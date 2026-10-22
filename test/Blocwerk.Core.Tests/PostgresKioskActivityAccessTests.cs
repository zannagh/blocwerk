// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="KioskActivityAccessTests"/> on PostgreSQL, so the kiosk's activity rule is translated by the real provider.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresKioskActivityAccessTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task WallActivity_IsForMembersOnly_NeverTheRestingKioskOrAShareLink(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await KioskActivityAccessChecks.WallActivityAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task BoulderActivity_FollowsTheBoulder_NoDraftsOutside_NoArchivedOnTheTablet(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await KioskActivityAccessChecks.BoulderActivityAsync(h, viewer);
    }

    [PostgresFact]
    public async Task TheRestingKiosk_NeverReadsAnotherWallsActivity()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await KioskActivityAccessChecks.KioskStaysOnItsWallAsync(h);
    }

    [PostgresFact]
    public async Task TheRestingKiosk_ReadsNoArchivedBouldersComments()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await KioskActivityAccessChecks.ArchivedCommentsAsync(h);
    }

    [PostgresTheory]
    [InlineData(DraftViewer.KioskAnonymous)]
    [InlineData(DraftViewer.KioskActAsMember)]
    [InlineData(DraftViewer.Member)]
    [InlineData(DraftViewer.ShareAnonymous)]
    public async Task Bylines_TheRestingKioskNamesConsentingMembersAndSetters_ElseAMember(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await KioskBylineChecks.BylinesAsync(h, viewer);
    }
}
