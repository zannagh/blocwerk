using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="PhotoTextureRegistration"/>'s low-coverage exception: a fit whose inliers span little of the facet
/// in view is still accepted when they are many, precise and spread along both plane axes — and refused when they
/// are clustered or imprecise. The photo (4000 × 3000 px) maps onto an 8 × 6 m facet at 1 mm/px, shifted 1 m.
/// </summary>
public class PhotoTextureAcceptanceTests
{
    private const int Width = 4000;
    private const int Height = 3000;

    /// <summary>Offset directions: evenly spread, so no homography absorbs them.</summary>
    private const double GoldenAngle = 2.399963;

    private static readonly TexturePlaneFrame Frame = new("0", 0, 8000, 0, 6000, 8000, 6000);
    private static readonly PlaneRectMm Extent = new(0, 8000, 0, 6000);

    [Fact]
    public void ManyPreciseInliers_SpreadAlongBothAxes_AreAccepted_BelowTheCoverageBar()
    {
        var r = Register(TriangleOutline(1200, 0));

        Assert.True(r.Coverage < PhotoTextureRegistration.MinCoverage);
        Assert.True(r.Accepted, r.Reason);
        Assert.True(r.Inliers >= PhotoTextureRegistration.MinSpreadInliers);
    }

    [Fact]
    public void ClusteredInliers_AreRefused_HoweverMany()
    {
        var pairs = new List<PointCorrespondence>();
        for (var i = 0; i < 400; i++)
        {
            pairs.Add(Pair(1500 + (17 * (i % 20)), 1200 + (17 * (i / 20)), 0));
        }

        var r = Register(pairs);

        Assert.False(r.Accepted);
        Assert.Contains("of the facet in view", r.Reason);
    }

    [Fact]
    public void SpreadButImpreciseInliers_AreRefused()
    {
        // every match 7.5 mm off in an evenly spread direction: a fit that explains them only to within its
        // 8 mm inlier threshold keeps either too few inliers or too high an RMS to be trusted on its spread
        var r = Register(TriangleOutline(1200, 7.5));

        Assert.True(r.Coverage < PhotoTextureRegistration.MinCoverage);
        var imprecise = r.Inliers < PhotoTextureRegistration.MinSpreadInliers || r.RmsMm > PhotoTextureRegistration.MaxSpreadRmsMm;
        Assert.True(imprecise, $"{r.Inliers} inliers, RMS {r.RmsMm}");
        Assert.False(r.Accepted);
    }

    [Fact]
    public void SpreadAlongOneAxisOnly_IsRefused()
    {
        var pairs = new List<PointCorrespondence>();
        for (var i = 0; i < 300; i++)
        {
            pairs.Add(Pair(800 + (8 * i), 1500 + (7 * (i % 3)), 0));
        }

        var r = Register(pairs);

        Assert.False(r.Accepted);
    }

    /// <summary>The outline of a right triangle of inliers: legs along x and y, each <paramref name="length"/> px long.</summary>
    private static List<PointCorrespondence> TriangleOutline(double length, double offsetMm)
    {
        var pairs = new List<PointCorrespondence>();
        for (var i = 0; i < 120; i++)
        {
            var t = length * i / 119;
            var (p, q) = (GoldenAngle * 2 * i, GoldenAngle * ((2 * i) + 1));
            pairs.Add(Pair(1000 + t, 2000, Math.Cos(p) * offsetMm, Math.Sin(p) * offsetMm));
            pairs.Add(Pair(1000, 2000 - t, Math.Cos(q) * offsetMm, Math.Sin(q) * offsetMm));
            pairs.Add(Pair(1000 + t, 800 + t, Math.Cos(p + q) * offsetMm, Math.Sin(p + q) * offsetMm));
        }

        return pairs;
    }

    /// <summary>Photo px → texture px, a 1 m shift, the texture point moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    private static PointCorrespondence Pair(double x, double y, double dx, double dy = 0) => new(x, y, x + 1000 + dx, y + 1000 + dy);

    private static FacetRegistration Register(List<PointCorrespondence> pairs) =>
        PhotoTextureRegistration.Register(new PhotoTextureMatch(pairs, 50, null), Width, Height, Frame, Extent);
}
