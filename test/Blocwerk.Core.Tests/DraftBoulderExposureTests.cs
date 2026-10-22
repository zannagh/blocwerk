// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A draft boulder belongs to the wall's members. A share link (anonymous or followed by a signed-in non-member) and
/// the kiosk tablet with nobody picked must not learn it by id through any read: the wall load, the 3D view, the
/// boulder read or the historic reads. A member, and the kiosk acting as one, keep it. SQLite; see
/// <see cref="PostgresDraftBoulderExposureTests"/> for the same checks on PostgreSQL.
/// </summary>
public class DraftBoulderExposureTests
{
    public static TheoryData<DraftViewer> Viewers => DraftExposureChecks.Viewers;

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task WallLoad_CarriesDrafts_OnlyForMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await DraftExposureChecks.WallLoadAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task View3D_HighlightsADraft_OnlyForMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await DraftExposureChecks.View3DAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task BoulderRead_ReturnsADraft_OnlyToMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await DraftExposureChecks.BoulderReadAsync(h, viewer);
    }

    [Theory]
    [MemberData(nameof(Viewers))]
    public async Task HistoricReads_AnswerForADraft_OnlyToMembers(DraftViewer viewer)
    {
        using var h = new WallTestHarness();
        await DraftExposureChecks.HistoricReadsAsync(h, viewer);
    }

    [Fact]
    public async Task Builder_RefusesToHighlightADraft_ItWasHandedWithoutDraftRights()
    {
        // The second lock: even a wall that carries the draft (a member's load) is not drawn with it unless the
        // caller says the viewer may see drafts.
        using var h = new WallTestHarness();
        var s = await DraftExposureScenario.SeedAsync(h);
        var wall = (await s.LoadWallAsync(DraftViewer.Member))!;
        var doc = WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson);

        Assert.Null(Wall3DViewBuilder.Build(wall, doc, s.DraftId).BoulderName);
        Assert.Equal("Secret Draft", Wall3DViewBuilder.Build(wall, doc, s.DraftId, viewerSeesDrafts: true).BoulderName);
        Assert.Equal("Live One", Wall3DViewBuilder.Build(wall, doc, s.LiveId).BoulderName);
    }
}
