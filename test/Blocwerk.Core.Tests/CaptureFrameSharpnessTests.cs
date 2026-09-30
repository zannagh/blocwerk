// <copyright file="CaptureFrameSharpnessTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Sharpness of real encoded photos. Regression: a colour JPEG was decoded as Gray8, which Skia refuses for a
/// colour JPEG, so every capture photo scored 0 and the blur filter never saw a blurry photo.
/// </summary>
public class CaptureFrameSharpnessTests
{
    [Fact]
    public void Score_OfAColourJpeg_IsPositive_AndLowerWhenBlurred()
    {
        var sharp = CaptureFrameSharpness.Score(ColourJpeg(blurSigma: 0), 480);
        var blurred = CaptureFrameSharpness.Score(ColourJpeg(blurSigma: 4), 480);

        Assert.True(sharp > 100, $"sharp {sharp}");
        Assert.True(blurred > 0, $"blurred {blurred}");
        Assert.True(blurred < sharp / 4, $"blurred {blurred} vs sharp {sharp}");
    }

    [Fact]
    public void Score_OfSomethingThatIsNoImage_IsZero()
    {
        Assert.Equal(0, CaptureFrameSharpness.Score([1, 2, 3, 4]));
    }

    internal static byte[] ColourJpeg(float blurSigma)
    {
        using var surface = SKSurface.Create(new SKImageInfo(640, 480));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(200, 170, 120));
        using var paint = new SKPaint { IsAntialias = false };
        if (blurSigma > 0)
        {
            paint.ImageFilter = SKImageFilter.CreateBlur(blurSigma, blurSigma);
        }

        var colours = new[] { new SKColor(230, 40, 40), new SKColor(30, 60, 200), new SKColor(250, 220, 30) };
        for (var y = 0; y < 480; y += 24)
        {
            for (var x = (y / 24) % 2 * 24; x < 640; x += 48)
            {
                paint.Color = colours[((x / 48) + (y / 24)) % colours.Length];
                canvas.DrawRect(x, y, 24, 24, paint);
            }
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
