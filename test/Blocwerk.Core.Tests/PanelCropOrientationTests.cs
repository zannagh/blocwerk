// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.PanelCrop;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A crop of an EXIF-oriented photo: the rectangle is picked on the photo as the browser shows it (turned by the tag),
/// cut from the raw pixels, and the tag kept, so the cropped photo shows exactly that part of the displayed photo.
/// </summary>
public class PanelCropOrientationTests
{
    private static readonly SKColor[] Quadrants = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void CropOfTheDisplayedFrame_ShowsThatPartOfTheDisplayedPhoto(int orientation)
    {
        var source = StoredPhotoSanitizer.WithOrientation(QuadrantJpeg(200, 100), (ushort)orientation);
        var rect = new PanelCropRect(0.1, 0.2, 0.6, 0.7);

        var cropped = PanelCropImage.Crop(source, rect);

        using var shownOriginal = Displayed(source);
        using var shownCrop = Displayed(cropped.Photo);
        foreach (var (u, v) in new[] { (0.2, 0.2), (0.8, 0.2), (0.2, 0.8), (0.8, 0.8) })
        {
            var expected = Sample(shownOriginal, rect.Left + (u * rect.Width), rect.Top + (v * rect.Height));
            var actual = Sample(shownCrop, u, v);
            Assert.True(Close(expected, actual), $"orientation {orientation} at ({u},{v}): expected {expected}, got {actual}");
        }

        Assert.Equal(rect.Left, cropped.Rect.Left, 2);
        Assert.Equal(rect.Top, cropped.Rect.Top, 2);
        Assert.Equal(rect.Width, cropped.Rect.Width, 2);
        Assert.Equal(rect.Height, cropped.Rect.Height, 2);
        Assert.Equal(cropped.Rect, PanelCropImage.Snapped(source, rect));
    }

    [Theory]
    [InlineData(SKEncodedOrigin.TopRight)]
    [InlineData(SKEncodedOrigin.BottomRight)]
    [InlineData(SKEncodedOrigin.BottomLeft)]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    public void RawAndDisplayed_AreInverse(SKEncodedOrigin origin)
    {
        var rect = new PanelCropRect(0.1, 0.25, 0.3, 0.6);

        var back = PanelCropOrientation.ToDisplayed(PanelCropOrientation.ToRaw(rect, origin), origin);

        Assert.Equal(rect.Left, back.Left, 12);
        Assert.Equal(rect.Top, back.Top, 12);
        Assert.Equal(rect.Width, back.Width, 12);
        Assert.Equal(rect.Height, back.Height, 12);
    }

    /// <summary>The photo as the browser shows it: the rendition bakes the EXIF turn in, like the browser does.</summary>
    private static SKBitmap Displayed(byte[] photo)
    {
        var rendition = ImageRendition.Render(photo, 40) ?? throw new InvalidOperationException("no rendition");
        return SKBitmap.Decode(rendition.Bytes);
    }

    private static SKColor Sample(SKBitmap bitmap, double x, double y) =>
        bitmap.GetPixel((int)(x * (bitmap.Width - 1)), (int)(y * (bitmap.Height - 1)));

    private static bool Close(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) < 60 && Math.Abs(a.Green - b.Green) < 60 && Math.Abs(a.Blue - b.Blue) < 60;

    /// <summary>A raw JPEG in four solid quadrants (red, lime / blue, white), so any turn or misplaced cut shows.</summary>
    private static byte[] QuadrantJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Quadrants[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)]);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }
}
