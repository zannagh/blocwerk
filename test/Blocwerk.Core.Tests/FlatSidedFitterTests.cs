// <copyright file="FlatSidedFitterTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Has flat sides": from a noisy height field with missing cells whose outline is where the volume is higher than
/// 35 mm, the fitter must find the apex (a pyramid) or the ridge (a roof), extend the outline down to where the sheets
/// meet the wall, and give planar faces close to the truth; switching back restores the measured height field.
/// </summary>
public class FlatSidedFitterTests
{
    [Fact]
    public void Pyramid_GetsItsApex_ItsFourBaseCorners_AndFourFlatSides()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Pyramid, 1200, 1600, 800, 1150);

        var fit = FlatSidedFitter.Fit(field, footprint)!;

        var p = fit.Polyhedron;
        Assert.Equal("pyramid", p.Shape);
        var apex = Assert.Single(p.Top);
        Assert.InRange(SyntheticVolumes.Dist((apex.A, apex.B), (1400, 975)), 0, 25);
        Assert.InRange(apex.H, 138, 160);
        Assert.Equal(4, p.Base.Count);
        foreach (var corner in new[] { (1200.0, 800.0), (1600.0, 800.0), (1600.0, 1150.0), (1200.0, 1150.0) })
        {
            Assert.InRange(p.Base.Min(b => SyntheticVolumes.Dist(b, corner)), 0, 35);
        }

        Assert.Equal(4, p.SideCount);
        Assert.True(fit.IsGood);
        Assert.InRange(fit.RmsMm, 0, 8);
        AssertCloseToTruth(p, SyntheticVolumes.Pyramid, 1200, 1600, 800, 1150);
    }

    [Fact]
    public void Roof_GetsItsRidge_AndFlatSheetsDownToTheWall()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Roof, 1000, 1800, 800, 1200, seed: 5);

        var fit = FlatSidedFitter.Fit(field, footprint)!;

        var p = fit.Polyhedron;
        Assert.Equal("roof", p.Shape);
        Assert.Equal(2, p.Top.Count);
        var ends = p.Top.OrderBy(t => t.A).ToList();
        Assert.InRange(SyntheticVolumes.Dist((ends[0].A, ends[0].B), (1200, 1000)), 0, 40);
        Assert.InRange(SyntheticVolumes.Dist((ends[1].A, ends[1].B), (1600, 1000)), 0, 40);
        Assert.All(ends, e => Assert.InRange(e.H, 128, 150));
        Assert.Equal(4, p.Base.Count);
        foreach (var corner in new[] { (1000.0, 800.0), (1800.0, 800.0), (1800.0, 1200.0), (1000.0, 1200.0) })
        {
            Assert.InRange(p.Base.Min(b => SyntheticVolumes.Dist(b, corner)), 0, 40);
        }

        Assert.InRange(p.SideCount, 4, 6);
        Assert.True(fit.IsGood);
        AssertCloseToTruth(p, SyntheticVolumes.Roof, 1000, 1800, 800, 1200);
    }

    [Fact]
    public void BaseOutline_IsSimplifiedToItsCorners()
    {
        // The detector's outline of a triangular volume: the hull of 20 mm cells, many short edges along the slanted side.
        var cells = new List<(double A, double B)>();
        for (var a = 0.0; a < 600; a += 20)
        {
            for (var b = 0.0; b < 0.55 * (600 - a); b += 20)
            {
                cells.AddRange([(a, b), (a + 20, b), (a, b + 20), (a + 20, b + 20)]);
            }
        }

        var hull = PlanePolygon.ConvexHull(cells);
        var simple = VolumeRings.DropShortEdges(VolumeRings.Simplify(hull, FlatSidedFitter.SimplifyMm), 50);

        Assert.True(hull.Count > 6);
        Assert.Equal(3, simple.Count);

        // The staircase's outer envelope: b = 0.55 (600 − a) + 20, so the sharp corners are (636, 0) and (0, 350).
        foreach (var corner in new[] { (0.0, 0.0), (636.0, 0.0), (0.0, 350.0) })
        {
            Assert.InRange(simple.Min(p => SyntheticVolumes.Dist(p, corner)), 0, 30);
        }
    }

    [Fact]
    public void Toggle_StoresTheFaces_AndSwitchingOffRestoresTheHeightField()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Pyramid, 1200, 1600, 800, 1150);
        var measured = field.ToJson();
        var volume = new WallVolume
        {
            FacetId = "0",
            FootprintJson = System.Text.Json.JsonSerializer.Serialize(footprint.Select(p => new[] { p.A, p.B })),
            SurfaceJson = measured,
        };

        WallVolumeShapes.SetFlatSides(volume, on: true, force: false);
        var flat = VolumeSurface.FromJson(volume.SurfaceJson)!;
        var summary = WallVolumeShapes.Summary(volume);
        WallVolumeShapes.SetFlatSides(volume, on: false, force: false);

        Assert.NotNull(flat.Polyhedron);
        Assert.Equal("pyramid", summary.Shape);
        Assert.Equal(4, summary.Faces);
        Assert.InRange(flat.HeightAt(1400, 975), 138, 160);
        Assert.InRange(flat.HeightAt(1300, 975), 65, 85);
        Assert.Equal(0, flat.HeightAt(1100, 975));
        Assert.Equal(measured, volume.SurfaceJson);
        Assert.False(volume.HasFlatSides);
        Assert.Null(volume.HeightFieldJson);
    }

    [Fact]
    public void FlatFaces_GiveTheExactFaceNormal()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Pyramid, 1200, 1600, 800, 1150, noiseMm: 0, missing: 0);
        var p = FlatSidedFitter.Fit(field, footprint)!.Polyhedron;

        var n = p.NormalAt(1250, 975);
        var n2 = p.NormalAt(1300, 960);

        // The west face rises 150 mm over 200 mm: its normal leans to −a by atan(0.75), the same all over the face.
        Assert.InRange(n[0], -0.66, -0.54);
        Assert.InRange(Math.Abs(n[1]), 0, 0.05);
        Assert.Equal(n[0], n2[0], 6);
    }

    [Fact]
    public void RayGrazingAFlatFace_PutsTheHoldOnIt_ButNotOneFarAbove()
    {
        // A 400 mm square pyramid, apex (200, 200) at 150 mm; rays falling 1:10 along a over the apex.
        var p = new VolumePolyhedron(VolumeHull.Build([(0, 0), (400, 0), (400, 400), (0, 400)], [(200, 200, 150)]).Select(f => f.Vertices));
        var surface = VolumeSurface.FlatSided(p, 20);

        var grazing = surface.RayHit((-2800, 200, 470), 200 + 1700, 200, 15);
        var above = surface.RayHit((-2800, 200, 490), 200 + 1900, 200, 15);

        Assert.NotNull(grazing);
        Assert.InRange(grazing.Value.A, 190, 210);
        Assert.InRange(grazing.Value.H, 140, 151);
        Assert.Null(above);
    }

    private static void AssertCloseToTruth(VolumePolyhedron p, Func<double, double, double> truth, double a0, double a1, double b0, double b1)
    {
        var errors = new List<double>();
        for (var a = a0; a <= a1; a += 25)
        {
            for (var b = b0; b <= b1; b += 25)
            {
                errors.Add(Math.Abs(p.HeightAt(a, b) - truth(a, b)));
            }
        }

        Assert.InRange(errors.Average(), 0, 8);
    }
}
