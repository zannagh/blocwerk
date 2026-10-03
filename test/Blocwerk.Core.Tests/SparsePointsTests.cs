// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.Sparse;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Volumes and protrusion without a photo-real view: the reconstruction's sparse points are read from sparse.zip, kept in
/// a compact form, tied to the model's world through the photo centres, and measured on a coarser grid.
/// </summary>
public class SparsePointsTests
{
    private static readonly Dictionary<string, FacetFrame> Frames = new() { ["0"] = HoldFootprintEstimatorTests.Wall };

    private static readonly Dictionary<string, PlaneRectMm> Extents = new() { ["0"] = VolumeDetectorTests.Extent };

    private static readonly Dictionary<string, List<KnownHoldEllipse>> NoHolds = [];

    [Fact]
    public void Reader_KeepsThePhotoCentresAndTheMultiViewPoints_AndTheStoredFormRoundTrips()
    {
        var world = SparseFixture.Surface((_, _) => 0, stepMm: 200);

        var cloud = SparseCloudFile.Read(SparseCloudFile.Write(ColmapSparseReader.Read(SparseFixture.Zip(world))));

        Assert.Equal(["p01", "p02", "p03", "p04", "p05", "p06"], cloud.PhotoCentres.Keys.Order());
        Assert.Equal(world.Count, cloud.Count);
        Assert.True(Vec3.Distance(cloud.PhotoCentres["p05"], SparseFixture.WorldToColmap.Apply(SparseFixture.Photos["p05"])) < 1e-9);
        Assert.Equal(2, cloud.Track[0]);
        Assert.Equal(3, cloud.Track[^1]);
        Assert.Equal(0.5f, cloud.Error[7]);
        Assert.Throws<InvalidDataException>(() => ColmapSparseReader.Read([0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => SparseCloudFile.Read([1, 2, 3]));
    }

    [Fact]
    public void Alignment_BringsThePointsBackIntoTheWallWorld_WithoutTheTwoViewPoints()
    {
        var world = SparseFixture.Surface((_, _) => 0, stepMm: 200, noiseMm: 0);
        var cloud = ColmapSparseReader.Read(SparseFixture.Zip(world));

        var alignment = SparseWorldAlignment.Fit(cloud.PhotoCentres, SolvedCamera.ParseAll(SparseFixture.ModelJson()));

        Assert.NotNull(alignment);
        Assert.Equal(6, alignment.Used);
        Assert.True(alignment.RmsMm < 0.1);
        var back = SparseWorldAlignment.WorldPoints(cloud, alignment);
        Assert.Equal(world.Count - 3, back.Count);
        Assert.True(Math.Abs(back[0].X - world[3].X) < 0.01 && Math.Abs(back[0].Z - world[3].Z) < 0.01);
    }

    [Fact]
    public void Alignment_DropsAPhotoFarOff_AndNeedsFourSharedPhotos()
    {
        var cloud = ColmapSparseReader.Read(SparseFixture.Zip(SparseFixture.Surface((_, _) => 0, stepMm: 400)));
        var photos = SparseFixture.Photos.ToDictionary(kv => kv.Key, kv => kv.Value);
        photos["p03"] = [photos["p03"][0] + 900, photos["p03"][1], photos["p03"][2]];
        var moved = ColmapSparseReader.Read(SparseFixture.Zip([], photos));

        var fit = SparseWorldAlignment.Fit(moved.PhotoCentres, SolvedCamera.ParseAll(SparseFixture.ModelJson()));

        Assert.NotNull(fit);
        Assert.Equal(5, fit.Used);
        Assert.True(fit.RmsMm < 0.1);
        var three = cloud.PhotoCentres.Where(kv => kv.Key is "p01" or "p02" or "p03").ToDictionary();
        Assert.Null(SparseWorldAlignment.Fit(three, SolvedCamera.ParseAll(SparseFixture.ModelJson())));
    }

    [Fact]
    public void SparseVolumes_FindThePyramid_OnTheCoarseGrid_AndNothingOnAFlatWall()
    {
        var pyramid = SparseFixture.Surface(VolumeDetectorTests.PyramidAt, stepMm: 15);

        var found = VolumeDetector.Detect(pyramid, Frames, Extents, NoHolds, VolumeDetectionOptions.Sparse);

        var volume = Assert.Single(found, v => v.IsAccepted);
        Assert.InRange(volume.HeightMm, 85, 135);
        Assert.InRange(volume.Footprint.Average(p => p.A), 1330, 1470);
        Assert.InRange(volume.Surface!.HeightAt(1400, 950), 90, 135);
        var flat = SparseFixture.Surface((_, _) => 0, stepMm: 15, seed: 9);
        Assert.DoesNotContain(VolumeDetector.Detect(flat, Frames, Extents, NoHolds, VolumeDetectionOptions.Sparse), v => v.IsAccepted);
    }

    [Fact]
    public void SparseProtrusion_IsMeasuredFromAFewPoints_AndMarkedSparse()
    {
        // A 120 mm hold dome, 50 mm high, at (1500, 1000); sparse points every 15 mm with ±12 mm noise.
        static double Dome(double a, double b)
        {
            var r2 = (((a - 1500) * (a - 1500)) + ((b - 1000) * (b - 1000))) / (60.0 * 60);
            return r2 < 1 ? 50 * Math.Sqrt(1 - r2) : 0;
        }

        var points = SparseFixture.Surface(Dome, stepMm: 20);
        var outline = Enumerable.Range(0, 12).Select(i => new[] { 60 * Math.Cos(i * Math.PI / 6), 60 * Math.Sin(i * Math.PI / 6) }).ToList();
        var hold = new ProtrusionHold(Guid.NewGuid(), "0", 1500, 1000, outline, "k");

        var sparse = HoldProtrusionEstimator.Measure(points, Frames, [hold], null, Extents, HoldProtrusionTuning.Sparse)[hold.Id];

        Assert.Equal(HoldProtrusionSource.Sparse, sparse.Source);
        Assert.InRange(sparse.HeightMm, 20, 55);
        Assert.InRange(sparse.BaseMm, -10, 10);
        Assert.Contains("\"source\":\"Sparse\"", sparse.ToJson());
    }
}
