// <copyright file="PanelPhotoInfoLoaderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="PanelPhotoInfoLoader"/> reads a panel photo's size and EXIF focal length from its header alone, and
/// with them the 3D view's and the refinement's panel cameras (<see cref="HoldFootprintRefiner.PanelCameras"/>)
/// resolve a photo whose placed holds are all on one facet, which the DLT alone cannot (SQLite harness).
/// </summary>
public class PanelPhotoInfoLoaderTests
{
    private const int Width = 1008;
    private const int Height = 756;

    [Fact]
    public async Task SizeAndFocalLength_ComeFromThePhotoHeaderAlone()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var photo = Photo();
        var panelId = await SeedPanelAsync(harness, photo, []);

        Assert.True(photo.Length > PanelPhotoInfoLoader.HeaderBytes);
        Assert.NotNull(PanelPhotoInfo.FromImage(photo[..PanelPhotoInfoLoader.HeaderBytes])?.FocalPx);
        await using var db = harness.CreateContext();
        var key = new Wall3DPhotoKey(panelId, 0);
        var infos = await PanelPhotoInfoLoader.LoadAsync(db, harness.WallId, [key, new Wall3DPhotoKey(Guid.NewGuid(), 0)], CancellationToken.None);

        var info = Assert.Single(infos).Value;
        Assert.Equal((Width, Height), (info.Width, info.Height));
        Assert.Equal(14 / 36.0 * Width, info.FocalPx!.Value, 6);
    }

    [Fact]
    public async Task CoplanarPlacedHolds_GetAPanelCamera_OnlyWithThePhotoInfo()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var points = PanelCameraEstimatorTests.Holds(PanelCameraEstimatorTests.Centre, PanelCameraEstimatorTests.LookAt, "0", 60);
        var panelId = await SeedPanelAsync(harness, Photo(), points);

        await using var db = harness.CreateContext();
        var live = await db.Holds.AsNoTracking().Where(h => h.WallId == harness.WallId).ToListAsync();
        var photos = await PanelPhotoInfoLoader.LoadAsync(db, harness.WallId, live.Select(HoldPlaneProjector.PhotoOf), CancellationToken.None);
        var frames = PanelCameraEstimatorTests.Frames;

        Assert.Empty(HoldFootprintRefiner.PanelCameras(live, frames));
        var centre = HoldFootprintRefiner.PanelCameras(live, frames, photos)[new Wall3DPhotoKey(panelId, 0)];
        PanelCameraEstimatorTests.AssertNear(PanelCameraEstimatorTests.Centre, centre, 5);
    }

    /// <summary>A 1008 × 756 JPEG (larger than the header read) whose EXIF says 14 mm equivalent.</summary>
    private static byte[] Photo() => ExifJpeg.Build(TestImages.Noise(Width, Height), focal35: 14);

    private static async Task<Guid> SeedPanelAsync(WallTestHarness harness, byte[] photo, List<PlacedPhotoPoint> points)
    {
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = harness.WallId, Photo = photo, PhotoContentType = "image/jpeg", Generation = 0 };
        db.WallPanels.Add(panel);
        foreach (var p in points)
        {
            db.Holds.Add(new Hold
            {
                WallId = harness.WallId,
                WallPanelId = panel.Id,
                X = p.X,
                Y = p.Y,
                Radius = 0.01,
                FacetId = p.FacetId,
                PlaneAMm = p.A,
                PlaneBMm = p.B,
            });
        }

        await db.SaveChangesAsync();
        return panel.Id;
    }
}
