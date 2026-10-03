// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The C# side of the geometry kernel beyond the shared golden cases: the 3D view and the ray casts take the same voting
/// markers (a stray left out of the extent does not move a triangle's centroid) and the solver's fold-clipped outline of
/// a model without markers (<c>docs/geometry-kernel.md</c>).
/// </summary>
public sealed class GeometryKernelTests
{
    [Fact]
    public void VotingCorners_LeaveOutAMarkerFarOutsideTheExtent()
    {
        var doc = Doc(Golden("triangle-stray-marker"));
        var extent = doc.FindFacet("2")!.Value.Facet.ExtentMm;

        var corners = GeometryKernel.VotingCorners(doc, "2", extent);

        Assert.Equal(12, corners.Count); // markers 10, 11, 12; not the stray 13
        Assert.DoesNotContain(corners, c => c[0] > 100);
        Assert.Equal(16, GeometryKernel.VotingCorners(doc, "2", null).Count);
    }

    [Fact]
    public void MarkerCentroid_IgnoresTheStray()
    {
        var doc = Doc(Golden("triangle-stray-marker"));

        var centroid = Wall3DFacetOutlines.MarkerCentroid(doc, "2")!.Value;

        Assert.Equal((-1100 - 1300 - 500) / 3.0, centroid.A, 6);
        Assert.Equal((300 + 1000 + 150) / 3.0, centroid.B, 6);
    }

    [Fact]
    public void OutlineHalfPlanes_KeepOnlyTheFoldClip()
    {
        var extent = new PlaneRectMm(0, 1500, 0, 1500);
        IReadOnlyList<double[]> outline = [[0, 0], [1500, 0], [0, 1500]];

        var cuts = GeometryKernel.OutlineHalfPlanes(outline, extent);
        var clockwise = GeometryKernel.OutlineHalfPlanes([.. outline.Reverse()], extent);

        var (alpha, beta, gamma) = Assert.Single(cuts);
        Assert.Equal(-Math.Sqrt(0.5), alpha, 9);
        Assert.Equal(-Math.Sqrt(0.5), beta, 9);
        Assert.Equal(-1500 * Math.Sqrt(0.5), gamma, 6);
        Assert.Equal(cuts, clockwise);
    }

    [Fact]
    public void FacetShapes_UseTheSolvedOutlineOfAModelWithoutMarkers()
    {
        var doc = Doc(Golden("sfm-no-markers"));

        var outlines = FacetShapes.Outlines(doc, null);

        var side = outlines["2"];
        Assert.Equal(3, side.Count);
        Assert.True(FacetShapes.Covers(side, doc.FindFacet("2")!.Value.Facet.ExtentMm!.Value, 300, 300, 0));
        Assert.False(FacetShapes.Covers(side, doc.FindFacet("2")!.Value.Facet.ExtentMm!.Value, 1200, 1200, 20));
        Assert.False(outlines.ContainsKey("0"));
    }

    private static JsonElement Golden(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GeometryGolden", name + ".json")));
        return json.RootElement.Clone();
    }

    private static WallGeometryDocument Doc(JsonElement golden) => WallGeometryDocument.Parse(golden.GetProperty("model").GetRawText());
}
