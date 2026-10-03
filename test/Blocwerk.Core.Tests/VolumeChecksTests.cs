// <copyright file="VolumeChecksTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The verdicts on measured candidates: an edge that is only a seam to a coplanar neighbouring facet, a low candidate
/// that fills little of its outline, and a small bare one on a facet without located holds.
/// </summary>
public class VolumeChecksTests
{
    private static readonly PlaneRectMm Extent = VolumeDetectorTests.Extent;

    private static readonly VolumeDetectionOptions Options = new();

    [Fact]
    public void EdgeAtASeam_IsNotTheWallsEdge()
    {
        var atSeam = Volume(Rect(2700, 2990, 800, 1100), fill: 1, median: 100);

        Assert.Equal(DetectedVolume.Accepted, Judge(atSeam, Seams(Continuation())));
        Assert.Equal(DetectedVolume.Accepted, Judge(atSeam, Seams(Continuation(gapMm: 120))));
        Assert.Equal("rejected:edge", Judge(atSeam, null));
        Assert.Equal("rejected:edge", Judge(atSeam, Seams(Continuation(tiltDeg: 30))));
        Assert.Equal("rejected:edge", Judge(atSeam, Seams(Continuation(gapMm: 400))));
        Assert.Equal("rejected:edge", Judge(atSeam, Seams(Continuation(proudMm: 120))));
    }

    [Fact]
    public void EdgeWithoutAContinuation_IsStillRejected()
    {
        var seams = Seams(Continuation());

        // The top edge has no neighbour, nor has the top-right corner (the neighbour ends at the same height).
        Assert.Equal("rejected:edge", Judge(Volume(Rect(1200, 1500, 1700, 1990), fill: 1, median: 100), seams));
        Assert.Equal("rejected:edge", Judge(Volume(Rect(2700, 2990, 1700, 1990), fill: 1, median: 100), seams));
        Assert.Equal("rejected:edge", Judge(Volume(Rect(10, 300, 800, 1100), fill: 1, median: 100), seams));
    }

    [Fact]
    public void EdgeAbuttedByAClippedNeighbour_IsASeamAlongItsWholeLength()
    {
        // The neighbour covers only the lower half of the right edge but starts right at it (clipped at the midline).
        var lower = FacetSeams.Of(HoldFootprintEstimatorTests.Wall, Extent, [(Continuation(), new PlaneRectMm(0, 2000, 0, 1000))], Options);
        var aboveIt = Volume(Rect(2700, 2990, 1300, 1600), fill: 1, median: 100);

        Assert.Equal(DetectedVolume.Accepted, Judge(aboveIt, lower));
        Assert.Equal("rejected:edge", Judge(aboveIt, Seams(Continuation(gapMm: 120), new PlaneRectMm(0, 2000, 0, 1000))));
        Assert.Equal("rejected:edge", Judge(Volume(Rect(2700, 2990, 1700, 1990), fill: 1, median: 100), lower));
    }

    [Fact]
    public void LowSparseOutline_IsRejected_ConvexAndTallOnesKept()
    {
        // The Attic's false L: 46 % of its outline raised, 55 mm median, 0.38 m² (under the shallow-sheet limit).
        var lShape = Volume(Rect(1000, 1620, 500, 1120), fill: 0.46, median: 55, h90: 79);
        Assert.Equal("rejected:sparse", Judge(lShape, null));

        // A pyramid or a roof fills its outline; a tall volume is kept even when its raised cells are patchy.
        Assert.Equal(DetectedVolume.Accepted, Judge(Volume(Rect(1000, 1400, 500, 800), fill: 0.92, median: 60), null));
        Assert.Equal(DetectedVolume.Accepted, Judge(Volume(Rect(1000, 1400, 500, 800), fill: 0.5, median: 110), null));
        Assert.True(VolumeChecks.IsSparse(Volume(Rect(1000, 1400, 500, 800), fill: 0.55, median: 67, h90: 67), Options));
    }

    [Fact]
    public void BareStep_OnlyJudged_WhenTheFacetHasLocatedHolds()
    {
        var small = Volume(Rect(1000, 1200, 500, 700), fill: 1, median: 70);

        Assert.Equal("rejected:bare-step", VolumeChecks.Judge(small, Extent, Options, null, holdsLocated: true));
        Assert.Equal(DetectedVolume.Accepted, VolumeChecks.Judge(small, Extent, Options, null, holdsLocated: false));
    }

    /// <summary>A facet continuing <see cref="HoldFootprintEstimatorTests.Wall"/> to the right of a = 3000, 20 mm proud.</summary>
    internal static FacetFrame Continuation(double gapMm = 0, double tiltDeg = 0, double proudMm = 20)
    {
        var t = tiltDeg * Math.PI / 180;
        return FacetFrame.From(new WallGeometryFacet
        {
            Id = "2",
            Origin = [3000 + gapMm, -proudMm, 0],
            U = [Math.Cos(t), Math.Sin(t), 0],
            V = [0, 0, 1],
            Normal = [Math.Sin(t), -Math.Cos(t), 0],
        })!;
    }

    private static FacetSeams Seams(FacetFrame neighbour, PlaneRectMm? extent = null) =>
        FacetSeams.Of(HoldFootprintEstimatorTests.Wall, Extent, [(neighbour, extent ?? new PlaneRectMm(0, 2000, 0, 2000))], Options);

    private static string Judge(DetectedVolume c, FacetSeams? seams) => VolumeChecks.Judge(c, Extent, Options, seams, holdsLocated: false);

    private static (double A, double B)[] Rect(double a0, double a1, double b0, double b1) => [(a0, b0), (a1, b0), (a1, b1), (a0, b1)];

    private static DetectedVolume Volume((double A, double B)[] footprint, double fill, double median, double h90 = 130)
    {
        var area = fill * PlanePolygon.Area(footprint) / 1e6;
        return new("0", footprint, area, h90, median, 500, 0, 0, 0.8, string.Empty, null);
    }
}
