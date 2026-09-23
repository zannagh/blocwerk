using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A bad <c>wall-geometry.json</c> comes back as friendly messages — never an exception, never a row.
/// </summary>
public class WallGeometryValidationTests
{
    public static TheoryData<string, string> BadFiles() => new()
    {
        { string.Empty, "empty" },
        { "{ not json", "not valid" },
        { "[1, 2, 3]", "not valid" },
        { GlyphGeometryJson.Build(version: 2), "version 2" },
        { GlyphGeometryJson.Build(withMarkers: false), "no markers" },
        { GlyphGeometryJson.Build(withFacets: false), "no facets" },
        { GlyphGeometryJson.Build(markerSizeMm: 0), "Marker size" },
        { GlyphGeometryJson.Build(markerFacet: "9z"), "\"9z\"" },
        { GlyphGeometryJson.Build(reprojRmsPx: -1), "reprojection" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public async Task BadFile_IsRefusedWithAMessage_AndStoresNothing(string json, string expectedFragment)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var result = await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, json, notes: null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Model);
        Assert.Contains(result.Errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
        await using var db = h.CreateContext();
        Assert.False(await db.WallGeometryModels.AnyAsync());
    }

    [Fact]
    public void TooLargeFile_IsRefused()
    {
        var (document, errors) = WallGlyphService.ParseAndValidate(new string(' ', WallGlyphService.MaxJsonLength) + "{}");

        Assert.Null(document);
        Assert.Contains("2 MB", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheckedInSampleFixture_Validates()
    {
        var json = await File.ReadAllTextAsync(SamplePath());

        var (document, errors) = WallGlyphService.ParseAndValidate(json);

        Assert.Empty(errors);
        Assert.NotNull(document);
    }

    private static string SamplePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "test", "Blocwerk.HoldDetection.Tests", "Fixtures", "Glyphs", "wall-geometry.sample.json");
    }
}
