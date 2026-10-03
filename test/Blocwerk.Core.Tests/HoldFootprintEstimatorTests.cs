// <copyright file="HoldFootprintEstimatorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A synthetic hold: a 60 × 60 mm box standing 40 mm off a vertical facet. Its silhouette seen from a
/// camera, projected back onto the facet, is smeared away from that camera; intersecting two
/// well-separated views must recover the true footprint, and a lone view gets the approximate correction.
/// </summary>
public class HoldFootprintEstimatorTests
{
    internal const double Half = 30;
    internal const double Height = 40;

    /// <summary>Vertical facet: a along +x, b up (+z), normal toward the climber (−y).</summary>
    internal static readonly FacetFrame Wall = FacetFrame.From(new WallGeometryFacet
    {
        Id = "0",
        Origin = [0, 0, 0],
        U = [1, 0, 0],
        V = [0, 0, 1],
        Normal = [0, -1, 0],
    })!;

    private static readonly double[] FromBelow = [0, -1500, -1500];
    private static readonly double[] FromRight = [1400, -1500, 200];

    [Fact]
    public void OneSteepView_SmearsTheBox_AwayFromTheCamera()
    {
        var silhouette = Silhouette(FromBelow);
        var (bMin, bMax) = PlanePolygon.Extent(silhouette, 0, 1);

        Assert.Equal(-Half, bMin, 1.0);
        Assert.True(bMax > Half + 30, $"top edge {bMax:F1} should be smeared ~40 mm upward");
    }

    [Fact]
    public void TwoSeparatedViews_IntersectToTheTrueFootprint()
    {
        var fp = HoldFootprintEstimator.Estimate(
            Wall, (0, 0), View(FromBelow, "below"), [View(FromRight, "right")], "k");

        Assert.NotNull(fp);
        Assert.Equal(HoldFootprintSource.MultiView, fp!.Source);
        Assert.Equal(2, fp.Views);
        var ring = fp.Outline.Select(p => (p[0], p[1])).ToList();
        var (aMin, aMax) = PlanePolygon.Extent(ring, 1, 0);
        var (bMin, bMax) = PlanePolygon.Extent(ring, 0, 1);
        const double tolerance = 6;
        Assert.InRange(aMin, -Half - tolerance, -Half + tolerance);
        Assert.InRange(aMax, Half - tolerance, Half + tolerance);
        Assert.InRange(bMin, -Half - tolerance, -Half + tolerance);
        Assert.InRange(bMax, Half - tolerance, Half + tolerance);
        Assert.InRange(PlanePolygon.Area(ring), 0.8 * 3600, 1.2 * 3600);
    }

    [Fact]
    public void SingleView_FallsBackToTheApproximateCorrection()
    {
        var before = PlanePolygon.Extent(Silhouette(FromBelow), 0, 1);
        var fp = HoldFootprintEstimator.Estimate(Wall, (0, 0), View(FromBelow, "below"), [], "k");

        Assert.NotNull(fp);
        Assert.Equal(HoldFootprintSource.SingleViewCorrected, fp!.Source);
        Assert.Equal(1, fp.Views);
        var (bMin, bMax) = PlanePolygon.Extent(fp.Outline.Select(p => (p[0], p[1])).ToList(), 0, 1);
        Assert.Equal(before.Min, bMin, 1.0);
        Assert.True(bMax - bMin < before.Max - before.Min - 10, "the far side should be pulled in");
    }

    [Fact]
    public void NearlyTheSameViewpoint_IsNotEnoughSpread_AndFallsBack()
    {
        double[] beside = [60, -1500, -1500];
        var fp = HoldFootprintEstimator.Estimate(Wall, (0, 0), View(FromBelow, "a"), [View(beside, "b")], "k");

        Assert.Equal(HoldFootprintSource.SingleViewCorrected, fp!.Source);
    }

    [Fact]
    public void ViewOf_ReportsTheAngleToTheNormal_AndTheSmearDirection()
    {
        var v = HoldFootprintEstimator.ViewOf(Wall, (0, 0), FromBelow)!.Value;

        Assert.Equal(45, v.ThetaDeg, 0.1);
        Assert.Equal(0, v.Dir.A, 1e-9);
        Assert.Equal(1, v.Dir.B, 1e-9);
    }

    /// <summary>The box's eight corners projected from <paramref name="camera"/> onto the facet, as a hull.</summary>
    internal static List<(double A, double B)> Silhouette(double[] camera)
    {
        var corners = new List<(double A, double B)>();
        foreach (var a in new[] { -Half, Half })
        {
            foreach (var b in new[] { -Half, Half })
            {
                foreach (var lift in new[] { 0.0, Height })
                {
                    double[] p = [a, -lift, b];
                    var t = camera[1] / (camera[1] - p[1]);
                    corners.Add((camera[0] + (t * (p[0] - camera[0])), camera[2] + (t * (p[2] - camera[2]))));
                }
            }
        }

        return PlanePolygon.ConvexHull(corners);
    }

    private static FootprintView View(double[] camera, string label) => new(Silhouette(camera), camera, label);
}
