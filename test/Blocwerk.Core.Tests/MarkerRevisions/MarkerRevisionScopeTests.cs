// <copyright file="MarkerRevisionScopeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.MarkerPlanning;
using SkiaSharp;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// Photos and models never mix revisions: an old photo maps onto the model only through markers that are
/// unchanged between its revision and the model's, and a wall-update seed only ties two photos through
/// markers unchanged across the old photo, the new photo and the model — a filler replaced by a smaller
/// one under the same id is no seed.
/// </summary>
public class MarkerRevisionScopeTests
{
    [Fact]
    public async Task ObservationMapping_KeepsOnlyMarkersUnchangedSinceThePhotosRevision()
    {
        using var h = new WallTestHarness();
        var (_, rows) = await SeedAsync(h);

        await using var db = h.CreateContext();
        var scope = await MarkerRevisionScope.LoadAsync(db, h.WallId);

        Assert.Equal((1, 2), (scope.ModelRevision!.Value, scope.CurrentRevision!.Value));
        Assert.Equal([0, 1, 4, 5], Ids(await scope.FilterAsync(rows[1])));
        Assert.Equal([0, 1], Ids(await scope.FilterAsync(rows[2])));
        Assert.Equal([0, 1, 4, 5], Ids(await scope.FilterAsync(rows[0])));
    }

    [Fact]
    public async Task FreshDetection_OfTheCurrentRevision_DropsMarkersTheModelHasNotSeenYet()
    {
        using var h = new WallTestHarness();
        await SeedAsync(h);

        await using var db = h.CreateContext();
        var scope = await MarkerRevisionScope.LoadAsync(db, h.WallId);
        var detected = new[] { 0, 1, 4, 5, 44 }.Select(id => new Abstractions.DetectedMarker { Id = id, CornersPx = [], CornersNormalized = [], SidePx = 20, EdgeRatio = 1 }).ToList();

        Assert.Equal([0, 1], (await scope.FilterAsync(detected, scope.CurrentRevision)).Select(m => m.Id));
    }

    [Fact]
    public async Task OverlapSeed_UsesOnlyMarkersUnchangedAcrossBothPhotosAndTheModel()
    {
        using var h = new WallTestHarness();
        var (panels, _) = await SeedAsync(h);

        await using var db = h.CreateContext();
        var left = new OverlapSeedSide(panels[1].Id, false, Png(), []);
        var right = new OverlapSeedSide(panels[2].Id, true, Png(), []);
        var (l, r) = await OverlapSeedLoader.LoadPhotosAsync(db, h.WallId, left, right, withModel: true);
        var (sameL, sameR) = await OverlapSeedLoader.LoadPhotosAsync(db, h.WallId, left, left, withModel: true);

        Assert.Equal([0, 1], l!.Markers.Select(m => m.Id).Order());
        Assert.Equal([0, 1], r!.Markers.Select(m => m.Id).Order());
        Assert.Equal([0, 1, 4, 5], sameL!.Markers.Select(m => m.Id).Order());
        Assert.Equal(sameL.Markers.Count, sameR!.Markers.Count);
    }

    /// <summary>
    /// The wall: an older legacy model, then the revision-1 model (active); plan revision 1 (from the measured
    /// wall) and revision 2 (filler 4 shrunk to 60 mm under its id, 5 moved 80 mm, 44 added). Photos: panel 0
    /// legacy, panel 1 revision 1, panel 2 (staged) revision 2.
    /// </summary>
    private static async Task<(List<WallPanel> Panels, List<List<WallMarkerObservation>> Rows)> SeedAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);
        Assert.True((await glyphs.ImportGeometryAsync(h.WallId, RegistrationFixtures.Rev1Json, "legacy")).Succeeded);
        var planService = new MarkerPlanService(h.DbContextFactory, h.CurrentUser, Microsoft.Extensions.Logging.Abstractions.NullLogger<MarkerPlanService>.Instance);
        var rev1 = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(RegistrationFixtures.Rev1Json), AtticMarkerPlan.Photo);
        Assert.True((await planService.SavePlanAsync(h.WallId, rev1)).Saved);
        var rev1Model = await glyphs.ImportGeometryAsync(h.WallId, RegistrationFixtures.Rev1Json, "rev1", "capture", new GeometryImportOptions(1));
        Assert.True(rev1Model.Succeeded);
        var added = rev1.Markers.Single(m => m.Id == 0) with { Id = 44, XMm = 1500, YMm = 1500, Role = MarkerRole.Filler };
        var rev2 = rev1 with
        {
            Markers = rev1.Markers
                .Select(m => m.Id == 4 ? m with { SizeMm = 60 } : m.Id == 5 ? m with { YMm = m.YMm + 80 } : m)
                .Append(added)
                .ToList(),
        };
        Assert.Equal(2, (await planService.SavePlanAsync(h.WallId, rev2)).Revision);

        await using var db = h.CreateContext();
        var panels = Enumerable.Range(0, 3).Select(col => new WallPanel { WallId = h.WallId, Col = col, Row = 0, Generation = 1 }).ToList();
        db.WallPanels.AddRange(panels);
        var rows = new List<List<WallMarkerObservation>>
        {
            Rows(panels[0], false, null, 0, 1, 4, 5),
            Rows(panels[1], false, 1, 0, 1, 4, 5),
            Rows(panels[2], true, 2, 0, 1, 4, 5, 44),
        };
        db.WallMarkerObservations.AddRange(rows.SelectMany(r => r));
        await db.SaveChangesAsync();
        return (panels, rows);
    }

    private static List<WallMarkerObservation> Rows(WallPanel panel, bool staged, int? revision, params int[] ids) =>
        ids.Select((id, i) => new WallMarkerObservation
        {
            WallPanelId = panel.Id,
            PanelGeneration = panel.Generation,
            FromStagedPhoto = staged,
            MarkerId = id,
            PlanRevision = revision,
            CornersJson = JsonSerializer.Serialize(new[] { new[] { 0.1 + (0.1 * i), 0.1 }, [0.15 + (0.1 * i), 0.1], [0.15 + (0.1 * i), 0.15], [0.1 + (0.1 * i), 0.15] }),
            SidePx = 20,
        }).ToList();

    private static List<int> Ids(IEnumerable<WallMarkerObservation> rows) => rows.Select(r => r.MarkerId).Order().ToList();

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(400, 300);
        bitmap.Erase(new SKColor(90, 90, 120));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
