// <copyright file="CroppedCentrePhotoTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The photo of a cropped centre panel after the crop: an update archives the cropped photo for the generation it
/// retires (its holds are positioned on that one, so historic boulders are drawn in place), and a new photo uploaded
/// onto the panel drops the crop record, whose kept original belongs to the replaced photo.
/// </summary>
public class CroppedCentrePhotoTests
{
    private static readonly PanelCropRect Crop = new(0.2, 0.1, 0.8, 0.8);

    [Fact]
    public async Task AnUpdateAfterACrop_ArchivesTheCroppedCentrePhoto()
    {
        using var f = new PanelCropFixture();
        var matcher = await CroppedCentreUpdateFrameTests.CropThenStageAsync(f);
        var service = CroppedCentreUpdateFrameTests.Service(f, matcher);
        await service.ResumeAsync(f.Harness.WallId);

        await service.PromoteAsync(f.Harness.WallId, new BigUpdateConfirmation([], [], [], []));

        var cropped = (await f.LoadPanelAsync()).Photo!;
        await using var db = f.Harness.CreateContext();
        var archived = await db.WallResets.AsNoTracking().SingleAsync(r => r.WallId == f.Harness.WallId && r.Generation == 0);
        Assert.Equal(cropped, archived.PreviousPhoto);
        Assert.NotEqual(f.OriginalPhoto, archived.PreviousPhoto);
        Assert.Equal("image/jpeg", archived.PreviousPhotoContentType);
    }

    [Fact]
    public async Task ANewPhotoOnACroppedCentre_DropsTheCrop_AndMovesThePhotoRevision()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        Assert.True((await f.Service.CropAsync(f.Harness.WallId, f.PanelId, Crop, confirmRemovals: true)).Applied);
        var revisionAfterCrop = (await f.LoadPanelAsync()).PhotoRevision;

        await f.Harness.WallService.UploadPhotoAsync(f.Harness.WallId, PanelCropFixture.Jpeg(200, 150), "image/jpeg", autoDetect: false);

        var panel = await f.LoadPanelAsync();
        await using var db = f.Harness.CreateContext();
        var wall = await db.Walls.AsNoTracking().SingleAsync(w => w.Id == f.Harness.WallId);
        Assert.False(await db.WallPanelCrops.AnyAsync(c => c.WallPanelId == f.PanelId));
        Assert.Equal(wall.Photo, panel.Photo);
        Assert.True(panel.PhotoRevision > revisionAfterCrop);
        Assert.False((await f.Service.GetStateAsync(f.Harness.WallId, f.PanelId))!.IsCropped);
    }

    [Fact]
    public async Task ANewPhoto_LandsOnTheNewestCentrePanel_NotAnOlderGeneration()
    {
        using var f = new PanelCropFixture();
        await f.SeedAsync();
        Guid newer;
        await using (var db = f.Harness.CreateContext())
        {
            var panel = new WallPanel
            {
                WallId = f.Harness.WallId, Col = 0, Row = 0, Generation = 1, Photo = PanelCropFixture.Jpeg(40, 30), PhotoContentType = "image/jpeg",
            };
            db.WallPanels.Add(panel);
            (await db.Walls.SingleAsync(w => w.Id == f.Harness.WallId)).CurrentGeneration = 1;
            await db.SaveChangesAsync();
            newer = panel.Id;
        }

        var photo = PanelCropFixture.Jpeg(200, 150);
        await f.Harness.WallService.UploadPhotoAsync(f.Harness.WallId, photo, "image/jpeg", autoDetect: false);

        await using var after = f.Harness.CreateContext();
        var wallPhoto = await after.Walls.Where(w => w.Id == f.Harness.WallId).Select(w => w.Photo).SingleAsync();
        Assert.Equal(wallPhoto, await after.WallPanels.Where(p => p.Id == newer).Select(p => p.Photo).SingleAsync());
        Assert.Equal(f.OriginalPhoto, (await f.LoadPanelAsync()).Photo);
    }
}
