// <copyright file="WallVolumeEditsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall admin's corrections of a detected volume: removing a false one sends its holds back to the facet and a new
/// detection does not bring it back (until "Undo"); flat sides move the holds onto the planar faces and switching them
/// off restores the measured shape exactly; the wall setting gives new volumes flat sides where they fit; a kiosk may do none of it.
/// </summary>
public sealed class WallVolumeEditsTests
{
    [Fact]
    public async Task RemovedVolume_IsNotDetectedAgain_UntilItIsRestored()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        var service = s.Service();
        Assert.Equal(1, (await service.DetectAsync(h.WallId)).Volumes);
        var volume = await s.VolumeAsync();
        Assert.NotNull(await s.PlacementAsync(s.OnVolume));

        await service.SetRemovedAsync(h.WallId, volume.Id, true);
        var afterRemove = await s.PlacementAsync(s.OnVolume);
        var again = await service.DetectAsync(h.WallId);
        var listed = Assert.Single(await service.ListAsync(h.WallId));

        Assert.Null(afterRemove);
        Assert.Equal(0, again.Volumes);
        Assert.True(listed.IsRemoved);
        Assert.Equal(volume.Id, listed.Id);
        Assert.Null(await s.PlacementAsync(s.OnVolume));
        s.Queue.Received().Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(s.OnVolume)));

        await service.SetRemovedAsync(h.WallId, volume.Id, false);

        Assert.Equal(volume.Id, (await s.PlacementAsync(s.OnVolume))!.VolumeId);
        Assert.False(Assert.Single(await service.ListAsync(h.WallId)).IsRemoved);
    }

    [Fact]
    public async Task FlatSides_PlaceTheHoldOnAFace_AndSwitchingOffRestoresEverything()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        var service = s.Service();
        await service.DetectAsync(h.WallId);
        var measured = await s.VolumeAsync();
        var before = await s.PlacementAsync(s.OnVolume);

        var on = await service.SetFlatSidesAsync(h.WallId, measured.Id, true);
        var flat = await s.VolumeAsync();
        var onFaces = (await s.PlacementAsync(s.OnVolume))!;
        var polyhedron = VolumeSurface.FromJson(flat.SurfaceJson)!.Polyhedron!;
        var off = await service.SetFlatSidesAsync(h.WallId, measured.Id, false);

        Assert.Equal(1, on.FlatSided);
        Assert.True(flat.HasFlatSides);
        Assert.Equal(measured.SurfaceJson, flat.HeightFieldJson);
        Assert.InRange(onFaces.H - polyhedron.HeightAt(onFaces.A, onFaces.B), -1.5, 1.5);
        Assert.Equal(polyhedron.NormalAt(onFaces.A, onFaces.B).Select(x => Math.Round(x, 4)), onFaces.Normal);
        Assert.Equal(0, off.FlatSided);
        Assert.Equal(measured.SurfaceJson, (await s.VolumeAsync()).SurfaceJson);
        Assert.Equal(before, await s.PlacementAsync(s.OnVolume), PlacementComparer.Instance);
        Assert.Null(await s.PlacementAsync(s.Bare));
    }

    [Fact]
    public async Task WallSetting_GivesNewVolumesFlatSidesWhereTheyFit_AndAVolumeSwitchedBackStaysBack()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        var service = s.Service();
        await service.SetWallFlatSidesAsync(h.WallId, true, applyToAll: false);

        await service.DetectAsync(h.WallId);
        var found = await s.VolumeAsync();
        var measured = VolumeSurface.FromJson(found.HeightFieldJson ?? found.SurfaceJson)!;
        var expected = FlatSidedFitter.Fit(measured, Wall3DVolumes.Footprint(found.FootprintJson))!.IsGood;
        await service.SetFlatSidesAsync(h.WallId, found.Id, false);
        await service.DetectAsync(h.WallId);

        Assert.True(await service.GetWallFlatSidesAsync(h.WallId));
        Assert.Equal(expected, found.HasFlatSides);
        Assert.False((await s.VolumeAsync()).HasFlatSides);
    }

    [Fact]
    public async Task Kiosk_MayNotChangeVolumes()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        await s.Service().DetectAsync(h.WallId);
        var volume = await s.VolumeAsync();
        var kiosk = s.Service(AnonymousSettingFixture.KioskContextFor(true, h.WallId, Guid.NewGuid()));

        await Assert.ThrowsAsync<KioskRestrictedException>(() => kiosk.SetFlatSidesAsync(h.WallId, volume.Id, true));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => kiosk.SetRemovedAsync(h.WallId, volume.Id, true));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => kiosk.SetWallFlatSidesAsync(h.WallId, true, true));
        await using var db = h.CreateContext();
        Assert.False((await db.WallVolumes.SingleAsync()).HasFlatSides);
    }

    /// <summary>Placements compared by value (the record holds an array).</summary>
    private sealed class PlacementComparer : IEqualityComparer<HoldVolumePlacement?>
    {
        public static readonly PlacementComparer Instance = new();

        public bool Equals(HoldVolumePlacement? x, HoldVolumePlacement? y) => x?.ToJson() == y?.ToJson();

        public int GetHashCode(HoldVolumePlacement? obj) => obj?.ToJson().GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
