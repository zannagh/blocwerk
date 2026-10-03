// <copyright file="VolumeEditScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Sparse;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall whose active model found one volume (the test pyramid, in the capture's sparse points) with a hold on it and a
/// hold beside it; the volume service over it (optionally as a kiosk) and a substitute refinement queue.
/// </summary>
internal sealed class VolumeEditScenario : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-volume-edit-tests", Guid.NewGuid().ToString("N"));

    private VolumeEditScenario(WallTestHarness harness)
    {
        Harness = harness;
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storeDir;
        Files = new FileSystemCaptureFileStore(settings);
    }

    public WallTestHarness Harness { get; }

    public FileSystemCaptureFileStore Files { get; }

    public IHoldRefinementQueue Queue { get; } = Substitute.For<IHoldRefinementQueue>();

    public Guid OnVolume { get; private set; }

    public Guid Bare { get; private set; }

    public static async Task<VolumeEditScenario> CreateAsync(WallTestHarness harness)
    {
        var s = new VolumeEditScenario(harness);
        await s.SeedAsync();
        return s;
    }

    public WallVolumeService Service(IKioskContext? kiosk = null) =>
        new(Harness.DbContextFactory, Harness.CurrentUser, NullLogger<WallVolumeService>.Instance, Files, kiosk, Queue);

    public async Task<WallVolume> VolumeAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.WallVolumes.AsNoTracking().SingleAsync(v => !v.IsRemoved);
    }

    public async Task<HoldVolumePlacement?> PlacementAsync(Guid holdId)
    {
        await using var db = Harness.CreateContext();
        return HoldVolumePlacement.FromJson((await db.Holds.AsNoTracking().SingleAsync(h => h.Id == holdId)).VolumePlacementJson);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(storeDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private async Task SeedAsync()
    {
        await Harness.SeedWallAsync(holdCount: 0);
        await using var db = Harness.CreateContext();
        var model = new WallGeometryModel { WallId = Harness.WallId, Json = SparseFixture.ModelJson(), SchemaVersion = 1, Source = "capture", IsActive = true };
        db.WallGeometryModels.Add(model);
        var cloud = ColmapSparseReader.Read(SparseFixture.Zip(SparseFixture.Surface(VolumeDetectorTests.PyramidAt, stepMm: 15)));
        db.WallCaptures.Add(new WallCapture
        {
            WallId = Harness.WallId, CreatedByUserId = Harness.Owner.Id, Status = WallCaptureStatus.Succeeded, GeometryModelId = model.Id,
            SparsePointsStoredPath = await Files.SaveAsync(SparseCloudFile.Write(cloud), SparseCloudFile.Extension, default),
        });
        var onVolume = Hold(1330, 950);
        var bare = Hold(2300, 1000);
        db.Holds.AddRange(onVolume, bare);
        await db.SaveChangesAsync();
        (OnVolume, Bare) = (onVolume.Id, bare.Id);
    }

    private Hold Hold(double a, double b) => new()
    {
        WallId = Harness.WallId, X = a / 3000, Y = 1 - (b / 2000), Radius = 0.02, FacetId = "0", PlaneAMm = a, PlaneBMm = b, WidthMm = 60, HeightMm = 60,
    };
}
