// <copyright file="FlatSidesDeclinedTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Flat sides the fit declined are not an admin's choice: the looser bound for pyramids and roofs, the fit error kept on a
/// declined volume, and a new detection trying again where an admin's "off" is honoured.
/// </summary>
public sealed class FlatSidesDeclinedTests
{
    [Fact]
    public void PyramidOrRoof_IsGoodWithinTheLooseBound_OtherShapesAreNot()
    {
        List<(double A, double B)> square = [(0, 0), (400, 0), (400, 400), (0, 400)];
        var pyramid = new VolumePolyhedron(VolumeHull.Build(square, [(200, 200, 80)]).Select(f => f.Vertices));
        var plateau = new VolumePolyhedron(VolumeHull.Build(square, [(150, 150, 80), (250, 150, 80), (250, 250, 80), (150, 250, 80)]).Select(f => f.Vertices), "plateau");

        Assert.Equal("pyramid", pyramid.Shape);
        Assert.True(FlatSidedFitter.IsGoodFit(11.9, plateau));
        Assert.True(FlatSidedFitter.IsGoodFit(14.9, pyramid));
        Assert.False(FlatSidedFitter.IsGoodFit(16.5, pyramid));
        Assert.False(FlatSidedFitter.IsGoodFit(14.9, plateau));
    }

    [Fact]
    public void PoorFit_IsDeclined_WithItsErrorKept_AdminOffClearsIt()
    {
        var (field, footprint) = SyntheticVolumes.Detected(SyntheticVolumes.Crater, 1200, 1600, 800, 1200);
        var volume = new WallVolume
        {
            FacetId = "0",
            FootprintJson = JsonSerializer.Serialize(footprint.Select(p => new[] { p.A, p.B })),
            SurfaceJson = field.ToJson(),
        };

        var fit = WallVolumeShapes.SetFlatSides(volume, on: true, force: false);

        Assert.NotNull(fit);
        Assert.False(fit.IsGood);
        Assert.False(volume.HasFlatSides);
        Assert.Equal(fit.RmsMm, volume.FlatFitRmsMm);
        WallVolumeShapes.SetFlatSides(volume, on: false, force: false);
        Assert.Null(volume.FlatFitRmsMm);
    }

    [Fact]
    public async Task DeclinedLastTime_IsTriedAgain_OnTheNextDetection()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        var service = s.Service();
        await service.SetWallFlatSidesAsync(h.WallId, true, applyToAll: false);
        await service.DetectAsync(h.WallId);
        var found = await s.VolumeAsync();
        var measured = VolumeSurface.FromJson(found.HeightFieldJson ?? found.SurfaceJson)!;
        Assert.True(FlatSidedFitter.Fit(measured, Wall3DVolumes.Footprint(found.FootprintJson))!.IsGood);

        // As a run whose fit was poor left it: measured shape, no flat sides, the error kept.
        await using (var db = h.CreateContext())
        {
            var row = await db.WallVolumes.SingleAsync(v => v.Id == found.Id);
            (row.SurfaceJson, row.HeightFieldJson, row.HasFlatSides, row.FlatFitRmsMm) = (row.HeightFieldJson!, null, false, 25);
            await db.SaveChangesAsync();
        }

        await service.DetectAsync(h.WallId);

        Assert.True((await s.VolumeAsync()).HasFlatSides);
    }
}
