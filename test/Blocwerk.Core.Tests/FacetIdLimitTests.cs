using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>Hold.FacetId</c> is a <c>varchar(16)</c>: a longer facet id in an imported model made every later
/// photo upload on that wall fail at SaveChanges on Postgres (SQLite never enforces the length). Ids are
/// now limited to 1–16 of <c>[A-Za-z0-9_-]</c> at import, when a hold is measured, and in the texture
/// manifest a worker sends back — which also refuses non-finite numbers and caps its size.
/// </summary>
public class FacetIdLimitTests
{
    [Theory]
    [InlineData("seventeen-chars-x")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("façade")]
    [InlineData("5b\n")]
    public async Task Import_RefusesAFacetIdARowCannotHold(string badId)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var json = GlyphGeometryJson.Build().Replace("\"5b\"", JsonSerializer.Serialize(badId));

        var result = await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, json, notes: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("1–16 letters", StringComparison.Ordinal));
        await using var db = h.CreateContext();
        Assert.False(await db.WallGeometryModels.AnyAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5b")]
    [InlineData("Facet_16-chars-A")]
    public void ValidIds_Pass(string id) => Assert.True(WallGeometryValidator.IsValidFacetId(id));

    [Fact]
    public void Planner_NeverWritesAnOverlongFacetId()
    {
        var hold = new Hold { WallId = Guid.NewGuid() };

        HoldMetricPlanner.Apply(hold, new HoldMetric(40, 30, 900, "way-too-long-facet-id", 100, 200, HoldMetric.MultiMarker));

        Assert.Null(hold.FacetId);
        Assert.Null(hold.PlaneAMm);
        Assert.Equal(40, hold.WidthMm);

        HoldMetricPlanner.Apply(hold, new HoldMetric(40, 30, 900, "5a", 100, 200, HoldMetric.MultiMarker));
        Assert.Equal(("5a", 100.0), (hold.FacetId, hold.PlaneAMm));
    }

    [Fact]
    public void TextureManifest_SkipsBadIds_NonFiniteNumbers_AndDuplicates()
    {
        var facets = new JsonArray(
            Facet("0", "1"),
            Facet("0", "dup"),
            Facet("waytoolongfacetid", "2"),
            Facet("../x", "3"),
            Facet("5a", "NaN"),
            Facet("5b", "Infinity"),
            Facet("5c", "4", width: 0),
            Facet("5d", "5", width: 1e9));

        var parsed = Parse(facets);

        Assert.Equal(["0"], parsed.Select(e => e.FacetId));
        Assert.Equal(1, parsed[0].AMin);
    }

    [Fact]
    public void TextureManifest_IsCapped()
    {
        var facets = new JsonArray(Enumerable.Range(0, 500).Select(i => (JsonNode?)Facet($"f{i}", "1")).ToArray());

        Assert.Equal(CaptureComputeDocuments.MaxTextureFacets, Parse(facets).Count);
    }

    private static IReadOnlyList<TextureManifestEntry> Parse(JsonArray facets)
    {
        using var doc = JsonDocument.Parse(new JsonObject { ["facets"] = facets }.ToJsonString());
        return CaptureComputeDocuments.ParseTextureResult(doc.RootElement);
    }

    private static JsonObject Facet(string id, string aMin, double width = 100) => new()
    {
        ["facet"] = id,
        ["file"] = $"facet_{id.Length}.jpg",
        ["widthPx"] = width,
        ["heightPx"] = 100,
        ["bounds"] = new JsonObject { ["aMin"] = aMin, ["aMax"] = 10.0, ["bMin"] = 0.0, ["bMax"] = 10.0 },
    };
}
