// <copyright file="VolumeFootprintsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The footprint refinement's view of volumes on the synthetic pyramid (<see cref="VolumeDetectorTests"/>): the
/// tangent frame of a placed hold, walking a flat point back onto it, and a volume hiding a hold behind it.
/// </summary>
public class VolumeFootprintsTests
{
    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    [Fact]
    public void TangentFrame_SitsOnThePlacement_AndMatchesTheViewersTilt()
    {
        var p = new HoldVolumePlacement(Guid.NewGuid(), 1400, 950, 100, [0.6, 0, 0.8], 1450, 950, "panel");

        var t = VolumeFootprints.TangentFrame(Wall, p)!;

        Assert.Equal(Wall.ToWorld(1400, 950, 100), t.Origin);
        Assert.InRange(RayMathDot(t.U, t.Normal), -1e-9, 1e-9);
        Assert.True(RayMathDot(t.U, Wall.U) > 0.7);
        var cross = new[] { (t.Normal[1] * t.U[2]) - (t.Normal[2] * t.U[1]), (t.Normal[2] * t.U[0]) - (t.Normal[0] * t.U[2]), (t.Normal[0] * t.U[1]) - (t.Normal[1] * t.U[0]) };
        Assert.All(Enumerable.Range(0, 3), i => Assert.InRange(t.V[i] - cross[i], -1e-9, 1e-9));
    }

    [Fact]
    public void FlatPoint_WalksBackOntoTheTangentPlane()
    {
        var p = new HoldVolumePlacement(Guid.NewGuid(), 1400, 950, 100, [0, 0, 1], 0, 0, "panel");
        var t = VolumeFootprints.TangentFrame(Wall, p)!;
        double[] camera = [1400, -2000, 950];

        // Straight on: the flat point 20 mm right of the placement lands 20·(2000−100)/2000 mm right of it.
        var q = VolumeFootprints.ToPlane(t, camera, Wall.ToWorld(1420, 950))!.Value;

        Assert.InRange(q.A, 18.9, 19.1);
        Assert.InRange(q.B, -0.1, 0.1);
    }

    [Fact]
    public void VolumeBetweenCameraAndHold_Occludes()
    {
        var surface = VolumeDetector.Detect(
            VolumeDetectorTests.Scene(withMacro: false),
            new Dictionary<string, FacetFrame> { ["0"] = Wall },
            new Dictionary<string, PlaneRectMm> { ["0"] = VolumeDetectorTests.Extent },
            new Dictionary<string, List<KnownHoldEllipse>>()).Single(v => v.IsAccepted).Surface!;
        var volumes = new FacetVolumes(Wall, [surface]);
        var hold = Wall.ToWorld(1650, 950, 10);        // just right of the pyramid

        Assert.True(VolumeFootprints.Occluded([1000, -150, 950], hold, volumes));    // grazing past the pyramid from the left
        Assert.False(VolumeFootprints.Occluded([2200, -800, 950], hold, volumes));   // from the right: clear
        Assert.False(VolumeFootprints.Occluded([1650, -2500, 950], hold, volumes));  // straight on: clear
    }

    [Fact]
    public void ALineOfSightSkimmingAFlatFace_IsNotOccluded_ButOneThroughItIs()
    {
        // A 400 mm square pyramid, apex (200, 200) at 150 mm; the skimming ray passes 20 mm over the apex, inside the
        // flat-face grace that only tracing a hold's own body may use.
        var p = new VolumePolyhedron(VolumeHull.Build([(0, 0), (400, 0), (400, 400), (0, 400)], [(200, 200, 150)]).Select(f => f.Vertices));
        var volumes = new FacetVolumes(Wall, [VolumeSurface.FlatSided(p, 20)]);
        var target = Wall.ToWorld(1900, 200, 0);

        Assert.False(VolumeFootprints.Occluded(Wall.ToWorld(-2800, 200, 470), target, volumes));
        Assert.True(VolumeFootprints.Occluded(Wall.ToWorld(-2800, 200, 400), target, volumes));
    }

    private static double RayMathDot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
}
