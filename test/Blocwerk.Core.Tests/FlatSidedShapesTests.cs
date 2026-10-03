// <copyright file="FlatSidedShapesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Has flat sides" beyond an apex or a ridge: a flat top (a truncated pyramid, a roof with a ridge plank) becomes one
/// flat face between flat sides, several peaks all go into the hull and are reported, and a volume the admin asked for
/// gets flat sides whatever the fit while a newly detected one keeps the quality limit.
/// </summary>
public class FlatSidedShapesTests
{
    [Fact]
    public void TruncatedPyramid_GetsAFlatTopFace_AndFourFlatSides()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Truncated, 1200, 1700, 800, 1250);

        var fit = FlatSidedFitter.Fit(field, footprint)!;

        var p = fit.Polyhedron;
        Assert.Equal("plateau", p.Shape);
        Assert.Equal(5, p.SideCount);
        Assert.True(fit.IsGood, $"RMS {fit.RmsMm}");
        Assert.All(p.Top, t => Assert.InRange(t.H, 110, 130));
        Assert.InRange(p.HeightAt(1450, 1025), 112, 128);
        Assert.InRange(p.NormalAt(1450, 1025)[2], 0.99, 1);
        foreach (var corner in new[] { (1350.0, 950.0), (1550.0, 950.0), (1550.0, 1100.0), (1350.0, 1100.0) })
        {
            Assert.InRange(p.Top.Min(t => SyntheticVolumes.Dist((t.A, t.B), corner)), 0, 40);
        }

        foreach (var corner in new[] { (1200.0, 800.0), (1700.0, 800.0), (1700.0, 1250.0), (1200.0, 1250.0) })
        {
            Assert.InRange(p.Base.Min(b => SyntheticVolumes.Dist(b, corner)), 0, 45);
        }

        AssertCloseToTruth(p, SyntheticVolumes.Truncated, 1200, 1700, 800, 1250);
    }

    [Fact]
    public void RoofWithAFlatRidgePlank_KeepsThePlankFlat()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.FlatRidge, 1000, 1800, 800, 1300, seed: 7);

        var fit = FlatSidedFitter.Fit(field, footprint)!;

        var p = fit.Polyhedron;
        Assert.Equal("plateau", p.Shape);
        Assert.InRange(p.SideCount, 5, 6);
        Assert.True(fit.IsGood, $"RMS {fit.RmsMm}");
        Assert.InRange(p.HeightAt(1300, 1050), 120, 140);
        Assert.InRange(p.HeightAt(1500, 1000), 120, 140);
        Assert.InRange(Math.Abs(p.HeightAt(1300, 1050) - p.HeightAt(1500, 1100)), 0, 3);
        AssertCloseToTruth(p, SyntheticVolumes.FlatRidge, 1000, 1800, 800, 1300);
    }

    [Fact]
    public void TwoPeaks_BothGoIntoTheHull_AndAreReported()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.TwoPeaks, 1000, 1750, 800, 1150, seed: 3);
        var volume = Volume(field, footprint);

        var fit = WallVolumeShapes.SetFlatSides(volume, on: true, force: true)!;
        var summary = WallVolumeShapes.Summary(volume);

        var p = fit.Polyhedron;
        Assert.Equal("multi-peak", p.Shape);
        Assert.Equal("multi-peak", summary.Shape);
        Assert.InRange(p.HeightAt(1200, 975), 135, 160);
        Assert.InRange(p.HeightAt(1550, 975), 100, 135);
        Assert.Contains(p.Top, t => SyntheticVolumes.Dist((t.A, t.B), (1200, 975)) < 40);
        Assert.Contains(p.Top, t => SyntheticVolumes.Dist((t.A, t.B), (1550, 975)) < 40);
        Assert.Equal("multi-peak", VolumeSurface.FromJson(volume.SurfaceJson)!.Polyhedron!.Shape);
    }

    [Fact]
    public void AskedForFlatSides_TheyApplyDespiteAPoorFit_ButTheAutomaticDefaultRefuses()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Crater, 1150, 1650, 750, 1250);
        var automatic = Volume(field, footprint);
        var asked = Volume(field, footprint);

        var refused = WallVolumeShapes.SetFlatSides(automatic, on: true, force: false)!;
        var forced = WallVolumeShapes.SetFlatSides(asked, on: true, force: true)!;

        Assert.False(refused.IsGood);
        Assert.False(automatic.HasFlatSides);
        Assert.Equal(field.ToJson(), automatic.SurfaceJson);
        Assert.True(asked.HasFlatSides);
        Assert.Equal(forced.RmsMm, asked.FlatFitRmsMm);
        Assert.Equal(forced.RmsMm, WallVolumeShapes.Summary(asked).FitRmsMm);
        Assert.InRange(forced.RmsMm, FlatSidedFitter.GoodRmsShare * 150, 1000);
    }

    [Fact]
    public async Task ApplyToAll_GivesEveryVolumeFlatSides_WhateverTheFit()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        var service = s.Service();
        await service.DetectAsync(h.WallId);
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Crater, 1150, 1650, 750, 1250);
        await using (var db = h.CreateContext())
        {
            var row = await db.WallVolumes.SingleAsync();
            (row.SurfaceJson, row.FootprintJson) = (field.ToJson(), Volume(field, footprint).FootprintJson);
            await db.SaveChangesAsync();
        }

        var result = await service.SetWallFlatSidesAsync(h.WallId, true, applyToAll: true);
        var volume = await s.VolumeAsync();

        Assert.Equal(1, result.FlatSided);
        Assert.Equal(0, result.KeptHeightField);
        Assert.True(volume.HasFlatSides);
        Assert.True(volume.FlatFitRmsMm > FlatSidedFitter.GoodRmsMm);
    }

    private static WallVolume Volume(VolumeSurface field, List<(double A, double B)> footprint) => new()
    {
        FacetId = "0",
        FootprintJson = System.Text.Json.JsonSerializer.Serialize(footprint.Select(p => new[] { p.A, p.B })),
        SurfaceJson = field.ToJson(),
    };

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
