// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The draft-exposure assertions, one per read path, shared by the SQLite and the PostgreSQL runs: a share-link
/// viewer (anonymous or a signed-in non-member) and the anonymous kiosk get not-found or no draft; a member and a
/// kiosk acting as that member still get it.
/// </summary>
internal static class DraftExposureChecks
{
    public static TheoryData<DraftViewer> Viewers => new(Enum.GetValues<DraftViewer>());

    /// <summary>The wall load every consumer of <c>wall.Boulders</c> reads (the 3D view, the wall page's count).</summary>
    public static async Task WallLoadAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);

        var wall = await s.LoadWallAsync(viewer);

        Assert.NotNull(wall);
        Assert.Contains(wall!.Boulders, b => b.Id == s.LiveId);
        Assert.Equal(DraftExposureScenario.SeesDrafts(viewer), wall.Boulders.Any(b => b.Id == s.DraftId));
    }

    /// <summary>The 3D view asked to highlight the draft: no name, no id, no hold roles for a viewer without drafts.</summary>
    public static async Task View3DAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);

        var result = await s.View3D(viewer).BuildAsync(s.WallId, s.DraftId, s.TokenFor(viewer));

        Assert.Equal(Wall3DViewStatus.Ok, result.Status);
        var view = result.View!;
        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            Assert.Equal((s.DraftId, "Secret Draft"), (view.BoulderId!.Value, view.BoulderName!));
            Assert.Contains(view.Holds, x => x.Role == Wall3DHoldRole.Start);
        }
        else
        {
            Assert.Null(view.BoulderId);
            Assert.Null(view.BoulderName);
            Assert.All(view.Holds, x => Assert.Null(x.Role));
        }

        // The live boulder is highlighted for everyone, so the refusal is about drafts and nothing else.
        var live = await s.View3D(viewer).BuildAsync(s.WallId, s.LiveId, s.TokenFor(viewer));
        Assert.Equal("Live One", live.View!.BoulderName);
    }

    /// <summary>The boulder detail page's read: the share route's and the by-id one.</summary>
    public static async Task BoulderReadAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var boulders = s.Boulders(viewer);

        var draft = DraftExposureScenario.UsesShareLink(viewer)
            ? await boulders.GetBoulderByShareTokenAsync(s.DraftId, DraftExposureScenario.Token)
            : await boulders.GetBoulderAsync(s.DraftId);
        var live = DraftExposureScenario.UsesShareLink(viewer)
            ? await boulders.GetBoulderByShareTokenAsync(s.LiveId, DraftExposureScenario.Token)
            : await boulders.GetBoulderAsync(s.LiveId);

        Assert.NotNull(live);
        Assert.Equal(DraftExposureScenario.SeesDrafts(viewer), draft is not null);
    }

    /// <summary>The detail page's "Then" reads: the historic generation and the holds at it.</summary>
    public static async Task HistoricReadsAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h, historic: true);
        var boulders = s.Boulders(viewer);
        var token = s.TokenFor(viewer);

        Assert.Equal(0, await boulders.GetHistoricGenerationAsync(s.LiveId, token));
        var sees = DraftExposureScenario.SeesDrafts(viewer);
        Assert.Equal(sees ? 0 : null, await boulders.GetHistoricGenerationAsync(s.DraftId, token));
        Assert.Equal(sees, await boulders.GetBoulderHoldsAtGenerationAsync(s.DraftId, 0, token) is not null);
    }
}
