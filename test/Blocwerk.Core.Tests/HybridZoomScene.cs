// <copyright file="HybridZoomScene.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A synthetic wall for the hybrid zoom tests: the vertical facet of <see cref="HoldFootprintEstimatorTests.Wall"/>
/// (a along +x, b up, normal −y), a panel photo taken square-on from 5 m and capture cameras around it.
/// </summary>
internal static class HybridZoomScene
{
    public const int Width = 4000;
    public const int Height = 3000;
    public const double Focal = 3000;

    public static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    /// <summary>The wall point the panel photo is centred on.</summary>
    public static readonly double[] Target = [1000, 0, 1000];

    /// <summary>The panel photo's camera: square-on from 5 m (0.6 px/mm).</summary>
    public static readonly SolvedCamera Panel = LookAt("panel", [1000, -5000, 1000], Target);

    /// <summary>The view used throughout: the middle tenth of the panel (about 667 × 500 mm of wall).</summary>
    public static readonly ViewRect View = ViewRect.Create(0.45, 0.45, 0.1, 0.1)!.Value;

    /// <summary>A pinhole camera at <paramref name="centre"/> looking at <paramref name="target"/>, image y down.</summary>
    public static SolvedCamera LookAt(string name, double[] centre, double[] target, double[]? dist = null)
    {
        var z = Normalize(Vec3.Sub(target, centre));
        var x = Normalize(Cross(z, [0, 0, 1]));
        var y = Cross(z, x);
        double[] r = [x[0], x[1], x[2], y[0], y[1], y[2], z[0], z[1], z[2]];
        double[] t =
        [
            -((r[0] * centre[0]) + (r[1] * centre[1]) + (r[2] * centre[2])),
            -((r[3] * centre[0]) + (r[4] * centre[1]) + (r[5] * centre[2])),
            -((r[6] * centre[0]) + (r[7] * centre[1]) + (r[8] * centre[2])),
        ];
        return new SolvedCamera(name, Width, Height, [Focal, 0, Width / 2.0, 0, Focal, Height / 2.0, 0, 0, 1], dist ?? [0, 0, 0, 0, 0], r, t);
    }

    /// <summary>The wall point under a panel-normalised point, through the panel camera.</summary>
    public static double[]? PanelToWorld(double x, double y) =>
        Panel.PixelToPlane(Wall, x * Width, y * Height) is { } p ? Wall.ToWorld(p.A, p.B) : null;

    /// <summary>The wall point at (u, v) of a view.</summary>
    public static Func<double, double, double[]?> WorldAt(ViewRect view) => (u, v) =>
    {
        var (x, y) = view.At(u, v);
        return PanelToWorld(x, y);
    };

    /// <summary>The panel photo's resolution over the view.</summary>
    public static double PanelPxPerMm(ViewRect view)
    {
        (double U, double V)[] c = [(0, 0), (1, 0), (1, 1), (0, 1)];
        var world = c.Select(p => WorldAt(view)(p.U, p.V)!).ToList();
        var image = c.Select(p => view.At(p.U, p.V)).Select(p => (p.X * Width, p.Y * Height)).ToList();
        return CloseUpCameraSelector.PxPerMm(image, world);
    }

    public static CloseUpPhoto Photo(SolvedCamera camera, double? sharpness = null) => new(camera.Image, camera, sharpness);

    private static double[] Normalize(double[] v) => Vec3.Scale(v, 1 / Vec3.Length(v));

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];
}
