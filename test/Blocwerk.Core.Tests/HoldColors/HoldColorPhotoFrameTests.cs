// <copyright file="HoldColorPhotoFrameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services.PanelCrop;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// A hold's position is a fraction of the panel photo as it is shown. The colour must be read at that spot when the
/// file is stored turned (EXIF orientation), after the panel was cropped (photo and positions rewritten), and for an
/// old centre hold that has no panel of its own.
/// </summary>
public class HoldColorPhotoFrameTests
{
    [Fact]
    public async Task APhotoStoredTurned_IsReadUpright()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0, photo: TurnedOnItsSide(HoldColorScenario.Photo()));
        var blue = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        var yellow = await s.AddHoldAsync(panel, HoldColorScenario.YellowSpot);
        var green = await s.AddHoldAsync(panel, HoldColorScenario.GreenSpot);

        await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal(
            ("blue", "yellow", "green"),
            ((await s.GetAsync(blue)).Color, (await s.GetAsync(yellow)).Color, (await s.GetAsync(green)).Color));
    }

    [Fact]
    public async Task AfterAPanelCrop_HoldsAreReadOnTheCroppedPhoto()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0, photo: ToJpeg(HoldColorScenario.Photo()));
        var blue = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        var green = await s.AddHoldAsync(panel, HoldColorScenario.GreenSpot);
        var before = await s.GetAsync(blue);
        var crops = new PanelCropService(s.Harness.DbContextFactory, s.Harness.CurrentUser, NullLogger<PanelCropService>.Instance);

        // The crop cuts a strip off the left and the top: every hold's fractions change with it.
        var cropped = await crops.CropAsync(s.WallId, panel, new PanelCropRect(0.08, 0.1, 0.92, 0.9), confirmRemovals: true);
        Assert.True(cropped.Applied);
        var moved = await s.GetAsync(blue);
        Assert.NotEqual((before.X, before.Y), (moved.X, moved.Y));
        await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal(("blue", "green"), ((await s.GetAsync(blue)).Color, (await s.GetAsync(green)).Color));
    }

    [Fact]
    public async Task AnOldCentreHoldWithoutAPanel_IsReadOnTheLiveCentrePanelPhoto()
    {
        // The wall photo of the harness is not an image; the centre panel's is what the hold's position refers to.
        using var s = await HoldColorScenario.CreateAsync(oldHolds: 1);
        await s.AddPanelAsync(0);
        Guid legacy;
        await using (var db = s.Harness.CreateContext())
        {
            var hold = db.Holds.Single(h => h.WallId == s.WallId);
            (hold.X, hold.Y, hold.Radius) = (HoldColorScenario.RedSpot.X, HoldColorScenario.RedSpot.Y, 0.07);
            await db.SaveChangesAsync();
            legacy = hold.Id;
        }

        await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal("red", (await s.GetAsync(legacy)).Color);
    }

    private static byte[] ToJpeg(byte[] photo)
    {
        using var bitmap = SKBitmap.Decode(photo);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>
    /// The photo as a phone held sideways stores it: the pixel grid turned, plus EXIF orientation 6 ("row 0 is the
    /// right side, column 0 is the top"). Built pixel by pixel, so the test does not lean on the code it checks.
    /// </summary>
    private static byte[] TurnedOnItsSide(byte[] photo)
    {
        using var upright = SKBitmap.Decode(photo);
        using var stored = new SKBitmap(upright.Height, upright.Width);
        for (var row = 0; row < stored.Height; row++)
        {
            for (var column = 0; column < stored.Width; column++)
            {
                stored.SetPixel(column, row, upright.GetPixel(upright.Width - 1 - row, column));
            }
        }

        using var image = SKImage.FromBitmap(stored);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return TestImages.WithExifOrientation(data.ToArray(), 6);
    }
}
