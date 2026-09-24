// <copyright file="HoldProtrusionEstimatorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Synthetic splat surfaces on the vertical test facet (a along +x, b up, normal −y): a flat wall with a
/// dome-shaped hold, and the same hold on a 100 mm volume. The estimator must find the body, the apex
/// and the surface under the hold, and fall back to the size estimate where the scene has no points.
/// </summary>
public class HoldProtrusionEstimatorTests
{
    private static readonly Dictionary<string, FacetFrame> Frames = new() { ["0"] = HoldFootprintEstimatorTests.Wall };

    private static readonly IReadOnlyList<double[]> Square =
        [[-40, -40], [40, -40], [40, 40], [-40, 40]];

    [Fact]
    public void DomeOnTheWall_GivesBodyApexAndAFlatBase()
    {
        var points = Surface(volumeMm: 0, holdA: 500, holdB: 800, apexOffsetA: 10);
        var hold = new ProtrusionHold(Guid.NewGuid(), "0", 500, 800, Square, "k");

        var p = HoldProtrusionEstimator.Measure(points, Frames, [hold])[hold.Id];

        Assert.Equal(HoldProtrusionSource.Splat, p.Source);
        Assert.InRange(p.BaseMm, -2, 2);
        Assert.InRange(p.ApexMm, 40, 50);
        Assert.InRange(p.HeightMm, 20, p.ApexMm);
        Assert.InRange(p.ApexA, 0, 20);
        Assert.False(p.OnVolume);
    }

    [Fact]
    public void HoldOnAVolume_HasTheVolumeAsItsBase()
    {
        var points = Surface(volumeMm: 100, holdA: 500, holdB: 800, apexOffsetA: 0);
        var hold = new ProtrusionHold(Guid.NewGuid(), "0", 500, 800, Square, "k");

        var p = HoldProtrusionEstimator.Measure(points, Frames, [hold])[hold.Id];

        Assert.InRange(p.BaseMm, 95, 105);
        Assert.InRange(p.ApexMm, 140, 150);
        Assert.True(p.OnVolume);
    }

    [Fact]
    public void HoldOnAVolume_MovesAlongTheFrontalCameraRayOntoIt()
    {
        // The photo saw the hold from 2 m out and 1 m to the left: its ray hits the facet plane 50 mm
        // right of where the hold (on a 100 mm volume) really is.
        var points = Surface(volumeMm: 100, holdA: 500, holdB: 800, apexOffsetA: 0);
        var flat = new ProtrusionHold(Guid.NewGuid(), "0", 550, 800, Square, "k");
        double[] camera = [500 - 1000, -2000, 800];
        double[] sideways = [500 + 3000, -800, 800];

        var p = HoldProtrusionEstimator.Measure(points, Frames, [flat], [sideways, camera])[flat.Id];

        Assert.True(p.OnVolume);
        Assert.InRange(p.ShiftA, -80, -40);
        Assert.InRange(p.ShiftB, -5, 5);
        Assert.InRange(p.ApexMm, 140, 150);
    }

    [Fact]
    public void NeighbouringFacetAtAFold_IsNotAVolume()
    {
        // A side wall folding 90° out of the facet at a = 560 mm (its surface is 80 mm "above" the facet
        // plane 80 mm left of the fold): the hold at a = 500 must not stand on it.
        var side = FacetFrame.From(new WallGeometryFacet { Id = "1", Origin = [560, 0, 0], U = [0, -1, 0], V = [0, 0, 1], Normal = [1, 0, 0] })!;
        var points = Surface(volumeMm: 0, holdA: 500, holdB: 800, apexOffsetA: 0)
            .Where(p => p.X <= 560)
            .Concat(Enumerable.Range(0, 80).SelectMany(i => Enumerable.Range(0, 80).Select(j => side.ToWorld(i * 5, 600 + (j * 5)))).Select(w => ((float)w[0], (float)w[1], (float)w[2])))
            .ToList();
        var frames = new Dictionary<string, FacetFrame>(Frames) { ["1"] = side };
        var hold = new ProtrusionHold(Guid.NewGuid(), "0", 500, 800, Square, "k");
        var extents = new Dictionary<string, PlaneRectMm> { ["0"] = new(0, 560, 0, 2000), ["1"] = new(0, 1000, 0, 2000) };

        var blind = HoldProtrusionEstimator.Measure(points, frames, [hold])[hold.Id];
        var aware = HoldProtrusionEstimator.Measure(points, frames, [hold], extents: extents)[hold.Id];

        Assert.True(blind.BaseMm > 20, $"without extents the side wall lifts the base to {blind.BaseMm:F1}");
        Assert.InRange(aware.BaseMm, -2, 2);
    }

    [Fact]
    public void HoldOutsideTheScene_IsEstimatedFromItsSize()
    {
        var points = Surface(volumeMm: 0, holdA: 500, holdB: 800, apexOffsetA: 0);
        var far = new ProtrusionHold(Guid.NewGuid(), "0", 3000, 3000, Square, "k");
        var unknownFacet = far with { Id = Guid.NewGuid(), FacetId = "9" };

        var result = HoldProtrusionEstimator.Measure(points, Frames, [far, unknownFacet]);

        Assert.Equal(HoldProtrusionSource.Estimate, result[far.Id].Source);
        Assert.Equal((0.16 * 80) + 14, result[far.Id].HeightMm, 3);
        Assert.False(result.ContainsKey(unknownFacet.Id));
    }

    [Fact]
    public void StoredProtrusion_IsDroppedOnceTheHoldMoves()
    {
        var hold = new Hold { X = 0.4, Y = 0.5, ShapePoints = [new ShapePoint { Dx = 0.01, Dy = 0 }] };
        var p = HoldProtrusion.Estimate(60, 60, HoldFootprint.KeyOf(hold));
        hold.ProtrusionMm = p.ToJson();

        Assert.Equal(p, HoldProtrusion.For(hold));

        hold.X = 0.41;
        Assert.Null(HoldProtrusion.For(hold));
    }

    /// <summary>
    /// A 400 × 400 mm patch of the facet at a 5 mm grid, raised by <paramref name="volumeMm"/>, with a
    /// 45 mm dome of radius 40 mm on it whose top sits <paramref name="apexOffsetA"/> mm right of centre.
    /// </summary>
    private static List<(float X, float Y, float Z)> Surface(double volumeMm, double holdA, double holdB, double apexOffsetA)
    {
        var points = new List<(float X, float Y, float Z)>();
        for (var a = holdA - 200; a <= holdA + 200; a += 5)
        {
            for (var b = holdB - 200; b <= holdB + 200; b += 5)
            {
                var r = Math.Sqrt(Math.Pow(a - holdA - apexOffsetA, 2) + Math.Pow(b - holdB, 2));
                var inHold = Math.Abs(a - holdA) <= 40 && Math.Abs(b - holdB) <= 40;
                var h = volumeMm + (inHold ? 45 * Math.Max(0, 1 - Math.Pow(r / 50, 2)) : 0);
                var w = HoldFootprintEstimatorTests.Wall.ToWorld(a, b, h);
                points.Add(((float)w[0], (float)w[1], (float)w[2]));
            }
        }

        return points;
    }
}
