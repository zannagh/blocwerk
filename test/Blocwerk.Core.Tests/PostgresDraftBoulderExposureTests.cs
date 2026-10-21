// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="DraftBoulderExposureTests"/> on PostgreSQL: the viewer-dependent draft predicates are translated by the
/// real provider, not only by SQLite.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresDraftBoulderExposureTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task WallLoad_CarriesDrafts_OnlyForMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await DraftExposureChecks.WallLoadAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task View3D_HighlightsADraft_OnlyForMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await DraftExposureChecks.View3DAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task BoulderRead_ReturnsADraft_OnlyToMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await DraftExposureChecks.BoulderReadAsync(h, viewer);
    }

    [PostgresTheory]
    [MemberData(nameof(Viewers))]
    public async Task HistoricReads_AnswerForADraft_OnlyToMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await DraftExposureChecks.HistoricReadsAsync(h, viewer);
    }
}
