// <copyright file="CroppedCentreUpdateFrameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A crop rewrites the centre PANEL photo and re-maps its holds into the cropped frame, but leaves
/// <see cref="Wall.Photo"/> as it was. An update staged afterwards must hand the carryover matcher the photo the
/// old holds are normalized to — the live centre panel photo — or every old hold is looked up in the wrong place.
/// </summary>
public class CroppedCentreUpdateFrameTests
{
    private static readonly PanelCropRect Crop = new(0.2, 0.1, 0.8, 0.8);

    [Fact]
    public async Task CentreCarryover_MatchesOnTheCroppedPanelPhoto_NotTheWallPhoto()
    {
        using var f = new PanelCropFixture();
        var matcher = await CropThenStageAsync(f);

        await Service(f, matcher).ResumeAsync(f.Harness.WallId);

        var panelPhoto = (await f.LoadPanelAsync()).Photo!;
        var (leftImage, _) = Assert.Single(matcher.Calls);
        Assert.Equal(panelPhoto, leftImage);
        Assert.NotEqual(f.OriginalPhoto, leftImage);
    }

    [Fact]
    public async Task CentreCarryover_OldHoldCoordinatesAgreeWithTheImageTheMatcherGets()
    {
        using var f = new PanelCropFixture();
        var matcher = await CropThenStageAsync(f);

        await Service(f, matcher).ResumeAsync(f.Harness.WallId);

        // The centre hold sat at pixel (200, 150) of the 400x300 original; the crop starts at pixel (80, 30).
        var (leftImage, leftHolds) = Assert.Single(matcher.Calls);
        var size = OverlapSeedLoader.RawSize(leftImage)!.Value;
        var centre = await f.LoadHoldAsync(f.Centre.Id);
        var seen = Assert.Single(leftHolds, h => Math.Abs(h.X - centre.X) < 1e-9 && Math.Abs(h.Y - centre.Y) < 1e-9);
        Assert.Equal(200 - 80, seen.X * size.Width, 0);
        Assert.Equal(150 - 30, seen.Y * size.Height, 0);
    }

    private static WallBigUpdateService Service(PanelCropFixture f, IHoldOverlapMatcher matcher) =>
        new(f.Harness.DbContextFactory, f.Harness.CurrentUser, f.Harness.HoldDetection, matcher, NullLogger<WallBigUpdateService>.Instance);

    /// <summary>The wall photo equals the centre panel photo (as after any promote), the centre is cropped, then re-shot.</summary>
    private static async Task<OldImageCapturingMatcher> CropThenStageAsync(PanelCropFixture f)
    {
        await f.SeedAsync();
        await using (var db = f.Harness.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == f.Harness.WallId);
            wall.Photo = f.OriginalPhoto;
            await db.SaveChangesAsync();
        }

        var cropped = await f.Service.CropAsync(f.Harness.WallId, f.PanelId, Crop, confirmRemovals: true);
        Assert.True(cropped.Applied);

        await using (var db = f.Harness.CreateContext())
        {
            var staged = new WallPanel
            {
                WallId = f.Harness.WallId, Col = 0, Row = 0, Generation = 1,
                StagedPhoto = PanelCropFixture.Jpeg(320, 240), StagedPhotoContentType = "image/jpeg",
            };
            db.WallPanels.Add(staged);
            db.Holds.Add(new Hold
            {
                WallId = f.Harness.WallId, WallPanelId = staged.Id, X = 0.4, Y = 0.5, Radius = 0.03, Generation = 1, IsAutoDetected = true,
            });
            await db.SaveChangesAsync();
        }

        return new OldImageCapturingMatcher();
    }
}
