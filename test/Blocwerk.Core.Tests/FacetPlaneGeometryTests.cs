// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An SfM facet's fold-clipped <c>outlineMm</c> moves with its frame and extent on every path that rewrites them (scale
/// and vertical corrections, the anchored registration's rebase, an extent grown over carried markers), and afterwards
/// still lines up with the extent's sides: one fold clip, every other edge on the extent (<c>docs/geometry-kernel.md</c>).
/// </summary>
public sealed class FacetPlaneGeometryTests
{
    private static readonly double R = Math.Sqrt(0.5);

    [Fact]
    public void ScaleCorrection_ScalesTheOutlineWithTheExtent()
    {
        var doc = WallGeometryDocument.Parse(WallGeometryModelTransformer.TransformDocument(SfmDoc(), GeometrySimilarity.Scaling(1.1)));

        var side = doc.FindFacet("2")!.Value.Facet;

        Assert.Equal(1650, side.ExtentMm!.Value.AMax, 1);
        AssertFoldAligned(side, -R, -R, -1650 * R);
    }

    [Fact]
    public void VerticalCorrection_KeepsTheOutline()
    {
        var turn = GeometrySimilarity.RotationBetween([0, 0, 1], [0.1, 0, 0.995], [0, 0, 0]);

        var doc = WallGeometryDocument.Parse(WallGeometryModelTransformer.TransformDocument(SfmDoc(), turn));

        AssertFoldAligned(doc.FindFacet("2")!.Value.Facet, -R, -R, -1500 * R);
    }

    [Fact]
    public void AnchoredRegistration_RebasesTheOutlineIntoTheReferenceFrame()
    {
        // the solved surface: the reference's plane, but its frame shifted by (500, 200) and turned 5° in-plane
        double c = Math.Cos(5 * Math.PI / 180), s = Math.Sin(5 * Math.PI / 180);
        var solved = JsonNode.Parse(MarkerlessFixture.FeatureDoc(anchored: true))!;
        var facet = solved["segments"]![0]!["facets"]![0]!.AsObject();
        facet["origin"] = new JsonArray(500.0, 0.0, 200.0);
        facet["u"] = new JsonArray(c, 0.0, s);
        facet["v"] = new JsonArray(-s, 0.0, c);
        facet["extentMm"] = new JsonObject { ["aMin"] = 0.0, ["aMax"] = 2000.0, ["bMin"] = 0.0, ["bMax"] = 2000.0 };
        facet["outlineMm"] = new JsonArray(new JsonArray(0.0, 0.0), new JsonArray(2000.0, 0.0), new JsonArray(0.0, 2000.0));

        var result = WallFrameRegistrationWriter.RewriteFeatures(solved.ToJsonString(), MarkerlessFixture.MarkerDoc(), Guid.NewGuid());

        var rebased = WallGeometryDocument.Parse(result.Json).FindFacet("0")!.Value.Facet;
        var cut = Assert.Single(GeometryKernel.OutlineHalfPlanes(rebased.OutlineMm, rebased.ExtentMm));
        foreach (var t in new[] { 200.0, 1000, 1800 })
        {
            // a point on the solved hypotenuse a + b = 2000, in world and then in the reference frame (origin 0, u = x, v = z)
            double a = t, b = 2000 - t;
            double x = 500 + (a * c) - (b * s), z = 200 + (a * s) + (b * c);
            Assert.Equal(cut.Gamma, (cut.Alpha * x) + (cut.Beta * z), 0);
        }

        AssertOnExtentOrFold(rebased, cut);
    }

    [Fact]
    public void GrownExtent_KeepsTheFoldAndMovesTheOtherSides()
    {
        var facet = JsonNode.Parse(SfmDoc())!["segments"]![1]!["facets"]![0]!.AsObject();

        FacetPlaneGeometry.Rewrite(facet, p => p, new PlaneRectMm(-200, 1500, 0, 1500));

        var side = WallGeometryDocument.Parse(new JsonObject { ["segments"] = new JsonArray(new JsonObject { ["facets"] = new JsonArray(facet.DeepClone()) }) }.ToJsonString())
            .FindFacet("2")!.Value.Facet;
        AssertFoldAligned(side, -R, -R, -1500 * R);
        Assert.Contains(side.OutlineMm!, p => p[0] == -200);
    }

    [Fact]
    public void AnExtentTheFoldNoLongerCuts_DropsTheOutline()
    {
        var facet = JsonNode.Parse(SfmDoc())!["segments"]![1]!["facets"]![0]!.AsObject();

        FacetPlaneGeometry.Rewrite(facet, p => p, new PlaneRectMm(0, 500, 0, 500));

        Assert.Null(facet["outlineMm"]);
        Assert.Equal(500, facet["extentMm"]!["aMax"]!.GetValue<double>());
    }

    private static string SfmDoc()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "GeometryGolden", "sfm-no-markers.json");
        return JsonNode.Parse(File.ReadAllText(path))!["model"]!.ToJsonString();
    }

    private static void AssertFoldAligned(WallGeometryFacet facet, double alpha, double beta, double gamma)
    {
        var cut = Assert.Single(GeometryKernel.OutlineHalfPlanes(facet.OutlineMm, facet.ExtentMm));
        Assert.Equal(alpha, cut.Alpha, 3);
        Assert.Equal(beta, cut.Beta, 3);
        Assert.Equal(gamma, cut.Gamma, 0);
        AssertOnExtentOrFold(facet, cut);
    }

    /// <summary>Golden-style: every outline corner lies on an extent side or on the fold, and inside the extent.</summary>
    private static void AssertOnExtentOrFold(WallGeometryFacet facet, (double Alpha, double Beta, double Gamma) cut)
    {
        var e = facet.ExtentMm!.Value;
        foreach (var p in facet.OutlineMm!)
        {
            var onSide = Math.Min(Math.Min(Math.Abs(p[0] - e.AMin), Math.Abs(p[0] - e.AMax)), Math.Min(Math.Abs(p[1] - e.BMin), Math.Abs(p[1] - e.BMax))) < 0.2;
            var onFold = Math.Abs((cut.Alpha * p[0]) + (cut.Beta * p[1]) - cut.Gamma) < 0.2;
            Assert.True(onSide || onFold, $"({p[0]}, {p[1]})");
            Assert.InRange(p[0], e.AMin - 0.1, e.AMax + 0.1);
            Assert.InRange(p[1], e.BMin - 0.1, e.BMax + 0.1);
        }
    }
}
