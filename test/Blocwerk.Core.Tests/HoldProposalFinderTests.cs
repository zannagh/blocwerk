// <copyright file="HoldProposalFinderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Five synthetic cameras look at the vertical test facet (a along +x, b up, normal −y). Holds are 3D points
/// 25 mm proud of the wall, "detected" by projecting them into every camera. Only a hold that is not known,
/// big enough and seen from several agreeing angles may come back as a proposal.
/// </summary>
public class HoldProposalFinderTests
{
    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    private static readonly CastFacet[] Facets = [new CastFacet("0", Wall, new PlaneRectMm(0, 3000, 0, 2000), [])];

    private static readonly Dictionary<string, SolvedCamera> Cameras = new()
    {
        ["p1"] = LookAt("p1", [1500, -3000, 1000], [1500, 0, 1000]),
        ["p2"] = LookAt("p2", [500, -2800, 1200], [1400, 0, 1000]),
        ["p3"] = LookAt("p3", [2600, -2800, 800], [1500, 0, 1000]),
        ["p4"] = LookAt("p4", [1500, -2500, 300], [1500, 0, 1000]),
        ["p5"] = LookAt("p5", [1200, -3200, 1800], [1500, 0, 1000]),
    };

    [Fact]
    public void UnknownHold_IsProposed_KnownHoldIsNot()
    {
        var known = Wall.ToWorld(1000, 900, 25);
        var fresh = Wall.ToWorld(1800, 1100, 25);
        var refs = new List<KnownHoldReference> { new(Guid.NewGuid(), Wall.ToWorld(1000, 900), known, 50) };

        var (candidates, clusters) = HoldProposalFinder.Find(Cameras, Detect(known, 60).Concat(Detect(fresh, 60)), Facets, refs, []);

        Assert.Equal(2, clusters);
        var c = Assert.Single(candidates);
        Assert.Equal(5, c.Views);
        Assert.InRange(RayMath.Length(c.World, fresh), 0, 5);
        Assert.InRange(c.SizeMm, 50, 70);
    }

    [Fact]
    public void FlatThings_TinyThings_AndReviewedSpots_AreNotProposed()
    {
        var flat = Wall.ToWorld(700, 700);             // a printed marker, a stain: on the wall itself
        var tiny = Wall.ToWorld(2200, 1400, 12);       // a bolt hole's rim
        var reviewed = Wall.ToWorld(1600, 500, 25);    // rejected before

        var (candidates, _) = HoldProposalFinder.Find(
            Cameras, Detect(flat, 60).Concat(Detect(tiny, 20)).Concat(Detect(reviewed, 60)), Facets, [], [reviewed]);

        Assert.Empty(candidates);
    }

    [Fact]
    public void DetectionsThatDisagree_DoNotMakeAHold()
    {
        // Each photo "sees" a different spot within the link distance: no point reprojects into all of them.
        var spots = new[] { (1500.0, 1000.0), (1530.0, 1000.0), (1500.0, 1030.0), (1470.0, 985.0), (1515.0, 970.0) };
        var detections = Cameras.Values.Zip(spots, (c, s) => DetectIn(c, Wall.ToWorld(s.Item1, s.Item2, 25), 40)).OfType<CaptureDetection>();

        var (candidates, _) = HoldProposalFinder.Find(Cameras, detections, Facets, [], []);

        Assert.Empty(candidates);
    }

    private static IEnumerable<CaptureDetection> Detect(double[] point, double sizeMm) =>
        Cameras.Values.Select(c => DetectIn(c, point, sizeMm)).OfType<CaptureDetection>();

    private static CaptureDetection? DetectIn(SolvedCamera c, double[] point, double sizeMm)
    {
        if (c.Project(point) is not { } px)
        {
            return null;
        }

        var dist = RayMath.Length(point, c.Centre);
        return new CaptureDetection(c.Image, px.X, px.Y, sizeMm / 2 * c.K[0] / dist, 0.9);
    }

    /// <summary>A 4000 × 3000 px pinhole camera at <paramref name="eye"/> looking at <paramref name="target"/>, z up.</summary>
    private static SolvedCamera LookAt(string name, double[] eye, double[] target)
    {
        var z = Unit([target[0] - eye[0], target[1] - eye[1], target[2] - eye[2]]);
        var x = Unit(Cross(z, [0, 0, 1]));
        var y = Cross(z, x);
        double[] r = [x[0], x[1], x[2], y[0], y[1], y[2], z[0], z[1], z[2]];
        double[] t = [-RayMath.Dot(x, eye), -RayMath.Dot(y, eye), -RayMath.Dot(z, eye)];
        return new SolvedCamera(name, 4000, 3000, [3000, 0, 2000, 0, 3000, 1500, 0, 0, 1], [0, 0, 0, 0, 0], r, t);
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double[] Unit(double[] v)
    {
        var n = Math.Sqrt(RayMath.Dot(v, v));
        return [v[0] / n, v[1] / n, v[2] / n];
    }
}
