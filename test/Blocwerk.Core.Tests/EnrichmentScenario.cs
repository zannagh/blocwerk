using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Seeding and one-shot runs for the enrichment tests. Every context is fresh (own connection).</summary>
internal static class EnrichmentScenario
{
    /// <summary>
    /// Two facets on the virtual 1000 px photo, both mapped fronto-parallel at 1.25 mm/px:
    /// facet "0" (markers 0 and 1, top band; a = 1.25x, b = 1.25(1000 - y)) and facet "2"
    /// (marker 12 only, bottom; a = 1.25x - 500, same b) with a wide extent that stops at b = 200.
    /// </summary>
    public const string TwoFacetGeometryJson = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [
            { "index": 0, "facets": [ { "id": "0", "extentMm": { "aMin": 0, "aMax": 1250, "bMin": 900, "bMax": 1250 } } ] },
            { "index": 2, "facets": [ { "id": "2", "extentMm": { "aMin": -1000, "aMax": 1000, "bMin": 200, "bMax": 1200 } } ] }
          ],
          "markers": [
            { "id": 0, "segment": 0, "facet": "0", "cornersPlaneMm": [[125, 1125], [250, 1125], [250, 1000], [125, 1000]] },
            { "id": 1, "segment": 0, "facet": "0", "cornersPlaneMm": [[1000, 1125], [1125, 1125], [1125, 1000], [1000, 1000]] },
            { "id": 12, "segment": 2, "facet": "2", "cornersPlaneMm": [[62.5, 250], [187.5, 250], [187.5, 125], [62.5, 125]] }
          ]
        }
        """;

    public static IMarkerDetectionService TwoFacetMarkers() => EnrichmentFakes.Markers(
        EnrichmentFakes.Square(0, 100, 100, 100),
        EnrichmentFakes.Square(1, 800, 100, 100),
        EnrichmentFakes.Square(12, 450, 800, 100));

    public static Action<Wall> Glyphs(double? markerSizeMm) => wall =>
    {
        wall.GlyphsEnabled = true;
        wall.MarkerSizeMm = markerSizeMm;
    };

    /// <summary>
    /// Builds auto-detected holds at <paramref name="positions"/>, enriches them as a staged photo of
    /// <paramref name="panelId"/> (generation 1), saves, and reads everything back through a new context.
    /// </summary>
    public static async Task<EnrichmentRun> RunAsync(
        WallTestHarness harness,
        IHoldEnrichmentService service,
        Action<Wall> configureWall,
        Guid? panelId,
        params (double X, double Y)[] positions)
    {
        return await RunAsync(harness, service, configureWall, panelId, [1, 2, 3], positions);
    }

    /// <summary>As <see cref="RunAsync(WallTestHarness, IHoldEnrichmentService, Action{Wall}, Guid?, ValueTuple{double, double}[])"/>, on a real photo.</summary>
    public static async Task<EnrichmentRun> RunAsync(
        WallTestHarness harness,
        IHoldEnrichmentService service,
        Action<Wall> configureWall,
        Guid? panelId,
        byte[] image,
        params (double X, double Y)[] positions)
    {
        HoldEnrichmentSummary summary;
        List<Guid> ids;
        await using (var db = harness.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == harness.WallId);
            configureWall(wall);
            var holds = positions.Select(p => EnrichmentFakes.AutoHold(harness.WallId, p.X, p.Y)).ToList();
            holds.ForEach(hold => hold.WallPanelId = panelId);
            db.Holds.AddRange(holds);
            summary = await service.EnrichAsync(
                db, new HoldEnrichmentRequest(image, wall, holds, panelId, 1, FromStagedPhoto: true));
            await db.SaveChangesAsync();
            ids = holds.Select(x => x.Id).ToList();
        }

        await using var read = harness.CreateContext();
        var stored = await read.Holds.Where(x => ids.Contains(x.Id)).ToListAsync();
        var observations = panelId is null
            ? await read.WallMarkerObservations.CountAsync()
            : await read.WallMarkerObservations.CountAsync(o => o.WallPanelId == panelId && o.FromStagedPhoto);
        return new EnrichmentRun(summary, ids.Select(id => stored.Single(x => x.Id == id)).ToList(), observations);
    }

    public static async Task<Guid> AddPanelAsync(WallTestHarness harness, byte[]? livePhoto = null)
    {
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = harness.WallId, Col = 0, Row = 0, Generation = 1, Photo = livePhoto };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    public static async Task AddObservationAsync(WallTestHarness harness, Guid panelId, bool fromStaged, int markerId)
    {
        await using var db = harness.CreateContext();
        db.WallMarkerObservations.Add(new WallMarkerObservation
        {
            WallPanelId = panelId,
            PanelGeneration = 1,
            FromStagedPhoto = fromStaged,
            MarkerId = markerId,
            CornersJson = "[]",
        });
        await db.SaveChangesAsync();
    }

    public static async Task AddActiveModelAsync(WallTestHarness harness, string json)
    {
        await using var db = harness.CreateContext();
        db.WallGeometryModels.Add(new WallGeometryModel
        {
            WallId = harness.WallId,
            Json = json,
            SchemaVersion = 1,
            Source = "test",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>One enrichment run, read back from the store.</summary>
internal sealed record EnrichmentRun(HoldEnrichmentSummary Summary, List<Hold> Holds, int ObservationCount);
