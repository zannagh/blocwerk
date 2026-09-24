using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A legacy panel wall that got a 3D model: two panel photos (c0 sees both facets, c1 matches nothing), an active
/// two-facet model with one texture per facet, and no markers anywhere. Photo c0 (4000 × 3000 px, 1 px/mm) shows
/// facet "0" on its left half and facet "1" on its right half, each 2000 × 3000 mm; the textures are 1 mm/px
/// with a 100 mm margin. So a hold at photo px (x, y) belongs at a = x (facet 0) or x − 2000 (facet 1), b = 3000 − y.
/// </summary>
internal sealed class HoldPlacementScenario
{
    public const string TwoFacetJson = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [
            { "index": 0, "facets": [ { "id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1],
              "extentMm": { "aMin": 0, "aMax": 2000, "bMin": 0, "bMax": 3000 } } ] },
            { "index": 1, "facets": [ { "id": "1", "origin": [2000, 0, 0], "u": [0.8, 0.6, 0], "v": [0, 0, 1],
              "extentMm": { "aMin": 0, "aMax": 2000, "bMin": 0, "bMax": 3000 } } ] }
          ],
          "markers": []
        }
        """;

    private HoldPlacementScenario(WallTestHarness harness)
    {
        Harness = harness;
    }

    public WallTestHarness Harness { get; }

    public Guid PanelC0 { get; private set; }

    public Guid PanelC1 { get; private set; }

    public Guid ModelId { get; private set; }

    public FakePhotoTextureMatcher Matcher { get; } = new();

    public ICaptureFileStore Files { get; } = Substitute.For<ICaptureFileStore>();

    public IHoldRefinementQueue Queue { get; } = Substitute.For<IHoldRefinementQueue>();

    public static async Task<HoldPlacementScenario> CreateAsync(WallTestHarness harness)
    {
        await harness.SeedWallAsync(holdCount: 0, generation: 1);
        var s = new HoldPlacementScenario(harness);
        await s.SeedAsync();
        s.Files.ReadAsync("t0.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 0 });
        s.Files.ReadAsync("t1.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });

        // Photo c0 (first byte 10): facet 0 on x ∈ [0, 2000], facet 1 (denser matches) on x ∈ [2000, 4000].
        s.Matcher.Views.Add(new FakeTextureView(10, 0, 0, 2000, 100, Shift(99.5)));
        s.Matcher.Views.Add(new FakeTextureView(10, 1, 2000, 4000, 80, Shift(99.5 - 2000)));
        return s;
    }

    public HoldTexturePlacementService Service(IKioskContext? kiosk = null) => new(
        Harness.DbContextFactory,
        Harness.CurrentUser,
        NullLogger<HoldTexturePlacementService>.Instance,
        Matcher,
        Files,
        Queue,
        kiosk);

    /// <summary>Adds a live circle hold on a panel photo at normalised (x, y).</summary>
    public async Task<Guid> AddHoldAsync(double x, double y, Guid? panel = null, Action<Hold>? configure = null)
    {
        await using var db = Harness.CreateContext();
        var hold = new Hold
        {
            WallId = Harness.WallId,
            WallPanelId = panel ?? PanelC0,
            X = x,
            Y = y,
            Radius = 0.01,
            Generation = 1,
            IsAutoDetected = true,
        };
        configure?.Invoke(hold);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    public async Task<Dictionary<Guid, Hold>> LoadHoldsAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.Holds.AsNoTracking().ToDictionaryAsync(h => h.Id);
    }

    /// <summary>photo px → texture px: a translation by (dx, 99.5) (1 px/mm both, texture margin 100 mm).</summary>
    private static PlaneHomography Shift(double dx) => PlaneHomography.FromCoefficients([1, 0, dx, 0, 1, 99.5, 0, 0, 1]);

    private static WallGeometryTexture Texture(Guid modelId, string facet, string path) => new()
    {
        GeometryModelId = modelId,
        FacetId = facet,
        StoredPath = path,
        AMin = -100,
        AMax = 2100,
        BMin = -100,
        BMax = 3100,
        WidthPx = 2200,
        HeightPx = 3200,
    };

    private async Task SeedAsync()
    {
        await using var db = Harness.CreateContext();
        (await db.Walls.SingleAsync()).GlyphsEnabled = true;
        var c0 = new WallPanel { WallId = Harness.WallId, Col = 0, Row = 0, Generation = 1, Photo = [10] };
        var c1 = new WallPanel { WallId = Harness.WallId, Col = 1, Row = 0, Generation = 1, Photo = [20] };
        var model = new WallGeometryModel { WallId = Harness.WallId, Json = TwoFacetJson, SchemaVersion = 1, Source = "test", IsActive = true };
        db.WallPanels.AddRange(c0, c1);
        db.WallGeometryModels.Add(model);
        db.WallGeometryTextures.AddRange(Texture(model.Id, "0", "t0.jpg"), Texture(model.Id, "1", "t1.jpg"));
        await db.SaveChangesAsync();
        (PanelC0, PanelC1, ModelId) = (c0.Id, c1.Id, model.Id);
    }
}
