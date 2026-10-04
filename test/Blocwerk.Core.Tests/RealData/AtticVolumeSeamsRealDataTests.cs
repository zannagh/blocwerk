// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests.RealData;

/// <summary>
/// Volume seams on The Attic's real model and its five real main-wall volumes (flat plywood pyramids and roofs): the
/// edge the leftover bit abuts is a seam along its whole length (a volume reaching into it is not cut off), the wall's
/// other edges are not, and every volume stays inside the main wall's extent with a flat-sided fit.
/// </summary>
public sealed class AtticVolumeSeamsRealDataTests
{
    private const double Band = 100;

    [Theory]
    [InlineData(5000, 300, true)]
    [InlineData(5030, 1200, true)]
    [InlineData(5000, 2300, true)]
    [InlineData(5000, 3200, false)]
    [InlineData(10, 1500, false)]
    [InlineData(2500, 3270, false)]
    [InlineData(2500, 10, false)]
    public void TheMainWallsRightEdge_IsASeamWithTheLeftoverBit_AndItsOtherEdgesAreNot(double a, double b, bool seam)
    {
        var facets = AtticRealData.Facets();
        var main = facets.Single(f => f.Facet.Id == "0");
        var seams = FacetSeams.Of(main.Frame, main.Extent, facets.Where(f => f.Facet.Id != "0").Select(f => (f.Frame, f.Extent)), new VolumeDetectionOptions());

        Assert.Equal(seam, seams.ContinuesAcross(a, b, Band));
    }

    [Fact]
    public void TheFiveRealVolumes_SitInsideTheMainWall_AndOnlyTheTwoRightRoofsReachTheSeam()
    {
        var facets = AtticRealData.Facets();
        var main = facets.Single(f => f.Facet.Id == "0");
        var seams = FacetSeams.Of(main.Frame, main.Extent, facets.Where(f => f.Facet.Id != "0").Select(f => (f.Frame, f.Extent)), new VolumeDetectionOptions());
        var volumes = Volumes();

        Assert.Equal(5, volumes.Count);
        var atTheEdge = new List<string>();
        foreach (var (name, _, footprint, _) in volumes)
        {
            Assert.True(footprint.All(p => p.A > main.Extent.AMin && p.A < main.Extent.AMax && p.B > main.Extent.BMin && p.B < main.Extent.BMax), name);
            var edge = footprint.Where(p => main.Extent.AMax - p.A < Band || p.A - main.Extent.AMin < Band || main.Extent.BMax - p.B < Band || p.B - main.Extent.BMin < Band).ToList();
            if (edge.Count > 0)
            {
                atTheEdge.Add(name);

                // It touches the seam to the leftover bit, and the wall goes on there: it is not cut off by the extent's end.
                Assert.All(edge, p => Assert.True(seams.ContinuesAcross(p.A, p.B, Band), $"{name} at {p.A:F0}, {p.B:F0}"));
            }
        }

        Assert.Equal(["lower-right roof", "upper-right roof"], atTheEdge.Order());
    }

    [Fact]
    public void TheFiveRealVolumes_AreFlatSided_WithGoodFits_AndTheirShapesAreSnapshotted()
    {
        var shapes = Volumes().Select(v => FlatSidedFitter.Fit(v.Field, v.Footprint)!)
            .Select(fit => (fit.Polyhedron.Shape, fit.IsGood)).ToList();

        Assert.All(shapes, s => Assert.True(s.IsGood));
        Assert.Equal(5, shapes.Count);
        Assert.Equal(Volumes().Select(v => v.Shape).Order(), shapes.Select(s => s.Shape).Order());
    }

    private static List<(string Name, string Shape, List<(double A, double B)> Footprint, VolumeSurface Field)> Volumes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Volumes", "attic-main-wall-volumes.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return [.. doc.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("name").GetString()!,
            e.GetProperty("shape").GetString()!,
            e.GetProperty("footprint").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList(),
            VolumeSurface.FromJson(e.GetProperty("field").GetRawText())!))];
    }
}
