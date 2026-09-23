using Blocwerk.Core.Geometry;

namespace Blocwerk.HoldDetection.Tests.Markers;

public class WallGeometryDocumentTests
{
    private static readonly string SamplePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs", "wall-geometry.sample.json");

    [Fact]
    public void Parse_ReadsTheSampleAndIgnoresUnknownFields()
    {
        var doc = WallGeometryDocument.Parse(File.ReadAllText(SamplePath));

        Assert.Equal(1, doc.Version);
        Assert.Equal("DICT_4X4_50", doc.Dictionary);
        Assert.Equal(125.0, doc.MarkerSizeMm);
        Assert.Equal(2, doc.Segments.Count);
        Assert.Equal(0.9, doc.Quality?.ReprojRmsPx);

        var main = doc.Segments[0];
        Assert.Equal(45.0, main.DeclaredAngleDeg);
        var facet = Assert.Single(main.Facets);
        Assert.Equal("0", facet.Id);
        Assert.Equal(new PlaneRectMm(-50, 3050, -50, 2550), facet.ExtentMm);
        Assert.Equal(new[] { 0, -0.7071, 0.7071 }, facet.Normal);

        Assert.Null(doc.Segments[1].DeclaredAngleDeg);
        Assert.Equal(["5a", "5b"], doc.Segments[1].Facets.Select(f => f.Id));
    }

    [Fact]
    public void Parse_ReadsMarkerCornersInTlTrBrBlOrder()
    {
        var doc = WallGeometryDocument.Parse(File.ReadAllText(SamplePath));

        var m0 = doc.FindMarker(0)!;
        Assert.Equal("TL", m0.Role);
        Assert.Equal("0", m0.Facet);
        Assert.Equal(4, m0.CornersPlaneMm.Count);
        Assert.Equal(new[] { 0.0, 2500.0 }, m0.CornersPlaneMm[0]);
        Assert.Equal(new[] { 0.0, 2375.0 }, m0.CornersPlaneMm[3]);
        Assert.False(m0.Synthetic);

        Assert.True(doc.FindMarker(1)!.Synthetic);
        Assert.Null(doc.FindMarker(33)!.CornersWorldMm);
        Assert.Null(doc.FindMarker(17));
        Assert.Equal(5, doc.FindFacet("5a")?.Segment.Index);
    }

    [Fact]
    public async Task LoadAsync_MatchesParse()
    {
        await using var stream = File.OpenRead(SamplePath);

        var doc = await WallGeometryDocument.LoadAsync(stream);

        Assert.Equal(3, doc.Markers.Count);
    }
}
