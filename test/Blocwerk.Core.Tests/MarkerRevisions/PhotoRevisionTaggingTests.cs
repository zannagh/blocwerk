// <copyright file="PhotoRevisionTaggingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.MarkerRevisions.RevisionFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// Panel photos are tagged with the revision their markers SHOW: revision 2 saved (and printed) but not yet
/// put up must not relabel photos of revision 1's sheets — or those photos lose the markers changed in 2.
/// </summary>
public class PhotoRevisionTaggingTests
{
    [Fact]
    public async Task SavedButNotSwapped_PhotoOfOldSheets_IsTaggedRevision1_AndMapsWithAllItsMarkers()
    {
        using var h = new WallTestHarness();
        await SeedAsync(h);

        var rows = await EnrichAsync(h, Photo(Rev1, 0, 1, 4, 5, 24).ToArray());

        Assert.All(rows, r => Assert.Equal((1, 1, 1), (r.PlanRevision, r.CompatibleRevisionFrom, r.CompatibleRevisionTo)));
        await using var db = h.CreateContext();
        var scope = await MarkerRevisionScope.LoadAsync(db, h.WallId);
        Assert.Equal(2, scope.CurrentRevision);
        Assert.Equal([0, 1, 4, 5, 24], (await scope.FilterAsync(rows)).Select(r => r.MarkerId).Order());
    }

    [Fact]
    public async Task PhotoShowingANewId_IsTaggedRevision2()
    {
        using var h = new WallTestHarness();
        await SeedAsync(h);

        var rows = await EnrichAsync(h, Photo(Rev2, 0, 1, 44, 24).ToArray());

        Assert.All(rows, r => Assert.Equal((2, 2, 2), (r.PlanRevision, r.CompatibleRevisionFrom, r.CompatibleRevisionTo)));
    }

    [Fact]
    public async Task PhotoOfOnlyUnchangedMarkers_IsTaggedCompatible()
    {
        using var h = new WallTestHarness();
        var plans = await SeedAsync(h);
        Assert.True(await plans.SetRevisionEffectiveAsync(h.WallId, 1, DateTimeOffset.UtcNow.AddDays(-10)));

        var rows = await EnrichAsync(h, Photo(Rev1, 0, 1, 24).ToArray());

        // Compatible with 1..2; revision 1 is the one marked as on the wall, so it is picked.
        Assert.All(rows, r => Assert.Equal((1, 1, 2), (r.PlanRevision, r.CompatibleRevisionFrom, r.CompatibleRevisionTo)));
    }

    [Fact]
    public async Task EffectiveFrom_RoundTripsThroughTheService()
    {
        using var h = new WallTestHarness();
        var plans = await SeedAsync(h);
        var when = new DateTimeOffset(2026, 9, 20, 18, 30, 0, TimeSpan.FromHours(2));

        var before = await plans.GetRevisionsAsync(h.WallId);
        Assert.True(await plans.SetRevisionEffectiveAsync(h.WallId, 2, when));
        var after = await plans.GetRevisionsAsync(h.WallId);
        Assert.True(await plans.SetRevisionEffectiveAsync(h.WallId, 2, null));
        var cleared = await plans.GetRevisionsAsync(h.WallId);

        Assert.All(before, r => Assert.Null(r.EffectiveFrom));
        Assert.False(before.Single(r => r.Revision == 2).IsOnWall);
        Assert.True(before.Single(r => r.Revision == 1).IsOnWall); // measured by the revision-1 model
        Assert.Equal(when, after.Single(r => r.Revision == 2).EffectiveFrom);
        Assert.True(after.Single(r => r.Revision == 2).IsOnWall);
        Assert.Null(cleared.Single(r => r.Revision == 2).EffectiveFrom);
        Assert.False(await plans.SetRevisionEffectiveAsync(h.WallId, 9, when));
    }

    private static async Task<List<Entities.WallMarkerObservation>> EnrichAsync(WallTestHarness h, Abstractions.DetectedMarker[] markers)
    {
        var panelId = await EnrichmentScenario.AddPanelAsync(h);
        var service = EnrichmentFakes.Service(EnrichmentFakes.Outlines(), EnrichmentFakes.Markers(markers));
        await EnrichmentScenario.RunAsync(h, service, EnrichmentScenario.Glyphs(125), panelId, (0.5, 0.5));
        await using var db = h.CreateContext();
        var rows = await db.WallMarkerObservations.Where(o => o.WallPanelId == panelId).ToListAsync();
        Assert.Equal(markers.Length, rows.Count);
        return rows;
    }
}
