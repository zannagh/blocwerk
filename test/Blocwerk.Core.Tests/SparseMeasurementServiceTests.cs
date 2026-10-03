// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Sparse;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall whose active model has no photo-real view (no GPU runner) but whose capture kept its sparse points: volumes are
/// found and the holds measured from them (marked sparse, coarser); a value measured in a photo-real view is never replaced
/// by a sparse one; without sparse points nothing happens, as before.
/// </summary>
public sealed class SparseMeasurementServiceTests : IDisposable
{
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-sparse-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemCaptureFileStore files;

    public SparseMeasurementServiceTests()
    {
        var settings = new BlocwerkSettings();
        settings.WallImage.StoragePath = storeDir;
        files = new FileSystemCaptureFileStore(settings);
    }

    [Fact]
    public async Task WithoutASplat_TheSparsePointsGiveTheVolume_AndTheHoldsProtrusion()
    {
        using var h = new WallTestHarness();
        var (onVolume, bare) = await SeedAsync(h, withSparse: true);

        var volumes = await VolumeService(h).DetectFromPipelineAsync(h.WallId);
        var protrusion = await ProtrusionService(h).MeasureFromPipelineAsync(h.WallId);

        Assert.NotNull(volumes);
        Assert.Equal(1, volumes.Volumes);
        Assert.True(volumes.FromSparsePoints);
        Assert.NotNull(protrusion);
        Assert.True(protrusion.FromSparsePoints);
        await using var db = h.CreateContext();
        var stored = await db.WallVolumes.SingleAsync();
        Assert.InRange(stored.HeightMm, 85, 135);
        var holds = await db.Holds.ToDictionaryAsync(x => x.Id);
        var bareP = HoldProtrusion.For(holds[bare])!;
        Assert.Equal(HoldProtrusionSource.Sparse, bareP.Source);
        Assert.InRange(bareP.HeightMm, 20, 55);
        Assert.Equal(HoldProtrusionSource.Sparse, HoldProtrusion.For(holds[onVolume])!.Source);
        Assert.True(HoldProtrusion.For(holds[onVolume])!.OnVolume);
    }

    [Fact]
    public async Task ASparseRun_NeverReplacesAPhotoRealMeasurement()
    {
        using var h = new WallTestHarness();
        var (_, bare) = await SeedAsync(h, withSparse: true);
        string splatJson;
        await using (var db = h.CreateContext())
        {
            var hold = await db.Holds.SingleAsync(x => x.Id == bare);
            splatJson = new HoldProtrusion(HoldProtrusionSource.Splat, 400, 0, 47, 0, 0, 55, HoldFootprint.KeyOf(hold)).ToJson();
            hold.ProtrusionMm = splatJson;
            await db.SaveChangesAsync();
        }

        await ProtrusionService(h).MeasureFromPipelineAsync(h.WallId);

        await using var check = h.CreateContext();
        Assert.Equal(splatJson, (await check.Holds.SingleAsync(x => x.Id == bare)).ProtrusionMm);
    }

    [Fact]
    public async Task WithoutSparsePoints_NothingIsMeasured_AsBefore()
    {
        using var h = new WallTestHarness();
        await SeedAsync(h, withSparse: false);

        Assert.Null(await VolumeService(h).DetectFromPipelineAsync(h.WallId));
        Assert.Null(await ProtrusionService(h).MeasureFromPipelineAsync(h.WallId));
        await using var db = h.CreateContext();
        Assert.Empty(await db.WallVolumes.ToListAsync());
        Assert.All(await db.Holds.ToListAsync(), x => Assert.Null(x.ProtrusionMm));
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

    /// <summary>The volume pyramid and a 50 mm hold dome in the sparse points; one hold on each. Returns (on the volume, bare).</summary>
    private static double Scene(double a, double b)
    {
        var r2 = (((a - 2300) * (a - 2300)) + ((b - 1000) * (b - 1000))) / (60.0 * 60);
        return VolumeDetectorTests.PyramidAt(a, b) + (r2 < 1 ? 50 * Math.Sqrt(1 - r2) : 0);
    }

    private async Task<(Guid OnVolume, Guid Bare)> SeedAsync(WallTestHarness h, bool withSparse)
    {
        await h.SeedWallAsync(holdCount: 0);
        await using var db = h.CreateContext();
        var model = new WallGeometryModel { WallId = h.WallId, Json = SparseFixture.ModelJson(), SchemaVersion = 1, Source = "capture", IsActive = true };
        db.WallGeometryModels.Add(model);
        var cloud = ColmapSparseReader.Read(SparseFixture.Zip(SparseFixture.Surface(Scene, stepMm: 15)));
        db.WallCaptures.Add(new WallCapture
        {
            WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, GeometryModelId = model.Id,
            SparsePointsStoredPath = withSparse ? await files.SaveAsync(SparseCloudFile.Write(cloud), SparseCloudFile.Extension, default) : null,
        });
        var onVolume = Hold(h, 1400, 950, 70);
        var bare = Hold(h, 2300, 1000, 120);
        db.Holds.AddRange(onVolume, bare);
        await db.SaveChangesAsync();
        return (onVolume.Id, bare.Id);
    }

    private static Hold Hold(WallTestHarness h, double a, double b, double size)
    {
        var hold = new Hold
        {
            WallId = h.WallId, X = a / 3000, Y = 1 - (b / 2000), Radius = 0.02, FacetId = "0", PlaneAMm = a, PlaneBMm = b,
            WidthMm = size, HeightMm = size,
        };
        var outline = Enumerable.Range(0, 12).Select(i => new[] { size / 2 * Math.Cos(i * Math.PI / 6), size / 2 * Math.Sin(i * Math.PI / 6) }).ToList();
        hold.FootprintMm = new HoldFootprint(HoldFootprintSource.MultiView, 3, 60, null, HoldFootprint.KeyOf(hold), outline).ToJson();
        return hold;
    }

    private WallVolumeService VolumeService(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<WallVolumeService>.Instance, files);

    private HoldProtrusionService ProtrusionService(WallTestHarness h) =>
        new(h.DbContextFactory, NullLogger<HoldProtrusionService>.Instance, files);
}
