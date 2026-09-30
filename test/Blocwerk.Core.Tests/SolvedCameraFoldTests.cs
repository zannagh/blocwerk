// <copyright file="SolvedCameraFoldTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A solved camera with k1 &gt; 0, k2 &lt; 0 folds back past a normalized radius of about 1.36: points far outside its
/// field of view would project, mirrored, into the image. The projection refuses them.
/// </summary>
public class SolvedCameraFoldTests
{
    private static readonly double[] Folding = [0.057, -0.076, 0, 0];

    [Fact]
    public void TheFoldRadius_IsTheFirstExtremumOfTheRadialModel()
    {
        Assert.InRange(Math.Sqrt(SolvedCamera.FoldRadius2(Folding)), 1.36, 1.37);
        Assert.Equal(double.PositiveInfinity, SolvedCamera.FoldRadius2([0.05, 0.01, 0, 0]));
        Assert.Equal(double.PositiveInfinity, SolvedCamera.FoldRadius2([]));
    }

    [Fact]
    public void APointPastTheFold_DoesNotProjectIntoTheImage()
    {
        // Looking along +z from the origin: normalized radius = lateral offset / depth.
        var camera = new SolvedCamera("p01", 4000, 3000, [1500, 0, 2000, 0, 1500, 1500, 0, 0, 1], Folding, [1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]);

        Assert.NotNull(camera.Project([500, 0, 1000]));   // r = 0.5, inside the fold
        Assert.Null(camera.Project([1800, 0, 1000]));     // r = 1.8: the polynomial would put it at x ≈ 3045 px, in frame
        Assert.NotNull((camera with { Dist = [0.057, 0.01, 0, 0] }).Project([1800, 0, 1000]));
    }
}
