// <copyright file="PanelCameraEstimatorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="PanelCameraEstimator"/> on a synthetic ultra-wide pinhole camera (4032 × 3024 px, f = 1568 px, principal
/// point at the centre) photographing holds on a main wall facet "0" (the plane y = 0, normal −y toward the camera)
/// and, for the non-planar case, a folded side facet "1".
/// </summary>
public class PanelCameraEstimatorTests
{
    internal const int Width = 4032;
    internal const int Height = 3024;
    internal const double Focal = 1568;

    internal static readonly double[] Centre = [1800, -2600, 1500];
    internal static readonly double[] LookAt = [2300, 0, 1300];
    internal static readonly Dictionary<string, FacetFrame> Frames = new(StringComparer.Ordinal)
    {
        ["0"] = Frame([0, 0, 0], [1, 0, 0]),
        ["1"] = Frame([4000, 0, 0], [0.7071068, -0.7071068, 0]),
    };

    private static readonly PanelPhotoInfo Photo = new(Width, Height, Focal);

    [Fact]
    public void CoplanarHolds_DegenerateTheDlt_ButThePlanarPoseRecoversTheCentre()
    {
        var points = Holds(Centre, LookAt, "0", 120);
        var world = points.Select(p => Frames["0"].ToWorld(p.A, p.B)).ToList();

        Assert.Null(CameraResection.Centre(world, points.Select(p => (p.X, p.Y)).ToList()));

        var estimate = PanelCameraEstimator.Estimate(points, Frames, Photo);

        Assert.StartsWith("planar", estimate.Method);
        AssertNear(Centre, estimate.Centre!, 3);
        Assert.Equal(2600, estimate.DistanceMm!.Value, 3.0);
        Assert.True(estimate.MedianErrorPx < 0.5);
    }

    [Fact]
    public void WithoutAFocalLength_ItIsSelfCalibratedFromTheHomography()
    {
        var points = Holds(Centre, LookAt, "0", 120);

        var estimate = PanelCameraEstimator.Estimate(points, Frames, Photo with { FocalPx = null });

        Assert.Equal("planar (self-calibrated focal)", estimate.Method);
        Assert.Equal(Focal, estimate.FocalPx!.Value, 2.0);
        AssertNear(Centre, estimate.Centre!, 5);
    }

    [Fact]
    public void AWrongExifFocalLength_LosesToTheSelfCalibratedOne()
    {
        var points = Holds(Centre, LookAt, "0", 120);

        var estimate = PanelCameraEstimator.Estimate(points, Frames, Photo with { FocalPx = 1200 });

        Assert.Equal("planar (self-calibrated focal)", estimate.Method);
        AssertNear(Centre, estimate.Centre!, 5);
    }

    [Fact]
    public void HoldsOnTwoFoldedFacets_StillUseTheDlt()
    {
        var points = Holds(Centre, LookAt, "0", 80).Concat(Holds(Centre, LookAt, "1", 80)).ToList();

        var estimate = PanelCameraEstimator.Estimate(points, Frames, null);

        Assert.Equal("dlt", estimate.Method);
        AssertNear(Centre, estimate.Centre!, 5);
    }

    [Fact]
    public void ACameraFarBeyondThePlausibleDistance_IsRejected()
    {
        double[] far = [1800, -30000, 1500];
        var points = Holds(far, LookAt, "0", 120, 20000);

        var estimate = PanelCameraEstimator.Estimate(points, Frames, Photo);

        Assert.Null(estimate.Centre);
        Assert.NotNull(estimate.Rejection);
    }

    [Fact]
    public void Check_RejectsACentreBehindTheWallOrWithALargeReprojectionError()
    {
        var world = new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1000, 0, 1000 } };
        var behind = new PanelCameraEstimate([500, 2000, 500], "dlt", 20, 20, null, 1, null, null);
        var inFront = behind with { Centre = [500, -2000, 500] };

        Assert.Null(PanelCameraEstimator.Check(behind, Frames["0"], world, Photo));
        Assert.Equal(2000, PanelCameraEstimator.Check(inFront, Frames["0"], world, Photo)!.DistanceMm!.Value, 6);
        Assert.Null(PanelCameraEstimator.Check(inFront with { MedianErrorPx = 200 }, Frames["0"], world, Photo));
    }

    /// <summary>Holds spread over a facet's 4 × 3 m, as seen by the camera, normalised to the photo; the ones in view.</summary>
    internal static List<PlacedPhotoPoint> Holds(double[] centre, double[] lookAt, string facet, int count, double spread = 4000)
    {
        var frame = Frames[facet];
        var result = new List<PlacedPhotoPoint>();
        for (var i = 0; i < count; i++)
        {
            var a = (i * 0.618034 % 1) * spread;
            var b = (i * 0.414214 % 1) * spread * 0.75;
            if (Project(centre, lookAt, frame.ToWorld(a, b)) is { } px && px.X >= 0 && px.X < Width && px.Y >= 0 && px.Y < Height)
            {
                result.Add(new PlacedPhotoPoint(facet, a, b, px.X / Width, px.Y / Height));
            }
        }

        return result;
    }

    internal static void AssertNear(double[] expected, double[] actual, double toleranceMm)
    {
        Assert.True(
            Math.Sqrt(Dot(Sub(expected, actual), Sub(expected, actual))) <= toleranceMm,
            $"expected ({string.Join(", ", expected)}), got ({string.Join(", ", actual.Select(v => v.ToString("F1")))})");
    }

    private static (double X, double Y)? Project(double[] centre, double[] lookAt, double[] world)
    {
        var forward = Normalize(Sub(lookAt, centre));
        var right = Normalize(Cross(forward, [0, 0, 1]));
        var down = Cross(forward, right);
        var d = Sub(world, centre);
        var z = Dot(forward, d);
        return z <= 0 ? null : ((Focal * Dot(right, d) / z) + (Width / 2.0), (Focal * Dot(down, d) / z) + (Height / 2.0));
    }

    private static FacetFrame Frame(double[] origin, double[] u) =>
        FacetFrame.From(new WallGeometryFacet { Id = "x", Origin = origin, U = u, V = [0, 0, 1] })!;

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Normalize(double[] a)
    {
        var n = Math.Sqrt(Dot(a, a));
        return [a[0] / n, a[1] / n, a[2] / n];
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];
}
