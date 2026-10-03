// <copyright file="FootprintCameraTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>The camera maths behind the footprints: resection, projection round trips, and the stored-footprint preference.</summary>
public class FootprintCameraTests
{
    /// <summary>Looking along +y from (500, −2000, 300): x_cam = x, y_cam = −z, z_cam = y.</summary>
    private static readonly SolvedCamera Camera = new(
        "IMG_1", 4000, 3000, [2000, 0, 2000, 0, 2000, 1500, 0, 0, 1], [0.01, -0.002, 0, 0, 0], [1, 0, 0, 0, 0, -1, 0, 1, 0], [-500, 300, 2000]);

    [Fact]
    public void Centre_IsMinusRTransposeT()
    {
        Assert.Equal(new[] { 500.0, -2000.0, 300.0 }, Camera.Centre);
    }

    [Fact]
    public void ProjectThenPixelToPlane_RoundTripsThroughDistortion()
    {
        var world = HoldFootprintEstimatorTests.Wall.ToWorld(320, 850);
        var px = Camera.Project(world)!.Value;
        var back = Camera.PixelToPlane(HoldFootprintEstimatorTests.Wall, px.X, px.Y)!.Value;

        Assert.Equal(320, back.A, 0.01);
        Assert.Equal(850, back.B, 0.01);
    }

    [Fact]
    public void Resection_RecoversTheCameraCentre_FromPointsOnTwoPlanes()
    {
        var world = new List<double[]>();
        var image = new List<(double X, double Y)>();
        for (var i = 0; i < 40; i++)
        {
            double[] p = i % 2 == 0 ? [(i * 53) % 1500, 0, (i * 97) % 1200] : [(i * 53) % 1500, -((i * 31) % 600), 0];
            var px = Camera.Project(p)!.Value;
            world.Add(p);
            image.Add((px.X / 4000, px.Y / 3000));
        }

        world.Add([700, 0, 600]);
        image.Add((0.9, 0.1)); // one gross outlier

        var c = CameraResection.Centre(world, image)!;
        Assert.Equal(500, c[0], 15.0);
        Assert.Equal(-2000, c[1], 15.0);
        Assert.Equal(300, c[2], 15.0);
    }

    [Fact]
    public void StoredFootprint_IsUsedOnlyWhileTheOutlineIsUnchanged()
    {
        var hold = new Hold { X = 0.5, Y = 0.5, ShapePoints = [new() { Dx = 0.01 }, new() { Dy = 0.01 }, new() { Dx = -0.01 }] };
        var fp = new HoldFootprint(HoldFootprintSource.MultiView, 3, 40, null, HoldFootprint.KeyOf(hold), [[0, 0], [10, 0], [0, 10]]);
        hold.FootprintMm = fp.ToJson();

        Assert.Equal(Wall3DShapeSource.Footprint, HoldShapeProjector.FromFootprint(HoldFootprint.For(hold))!.Source);

        hold.ShapePoints[0].Dx = 0.02;
        Assert.Null(HoldFootprint.For(hold));
    }
}
