// <copyright file="HybridZoomColorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Services.HybridZoom;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>The close-up's colours are matched to the panel photo, robustly, and only when that brings them closer.</summary>
public class HybridZoomColorTests
{
    [Fact]
    public void Fit_RecoversAWarmCast_DespiteOutliers()
    {
        var rng = new Random(7);
        var panel = Enumerable.Range(0, 400).Select(_ => new Rgb(20 + rng.Next(200), 20 + rng.Next(200), 20 + rng.Next(200))).ToList();

        // The close-up: warmer (red up, blue down), plus 10 % of samples on a hold that moved between the views.
        var close = panel.Select((p, i) => i % 10 == 0
            ? new Rgb(rng.Next(30, 220), rng.Next(30, 220), rng.Next(30, 220))
            : new Rgb((p.R * 1.10) + 8, p.G, (p.B * 0.85) - 4)).ToList();

        var fit = ColorMatch.Fit(close, panel)!;

        Assert.Equal(1 / 1.10, fit.R.Gain, 0.02);
        Assert.Equal(1.0, fit.G.Gain, 0.02);
        Assert.Equal(1 / 0.85, fit.B.Gain, 0.03);
        var clean = Enumerable.Range(0, 400).Where(i => i % 10 != 0).ToList();
        var after = ColorMatch.MeanDeltaE([.. clean.Select(i => fit.Apply(close[i]))], [.. clean.Select(i => panel[i])]);
        Assert.True(after < 1.0, $"ΔE after {after}");
    }

    [Fact]
    public void Fit_IgnoresClippedSamples_AndNeedsEnoughOfTheRest()
    {
        var white = Enumerable.Repeat(new Rgb(255, 255, 255), 500).ToList();
        Assert.Null(ColorMatch.Fit(white, white));
    }

    [Fact]
    public void DeltaE_IsHundredFromBlackToWhite_AndZeroForTheSame()
    {
        Assert.Equal(100, ColorMatch.MeanDeltaE([new Rgb(0, 0, 0)], [new Rgb(255, 255, 255)]), 0.1);
        Assert.Equal(0, ColorMatch.MeanDeltaE([new Rgb(120, 80, 40)], [new Rgb(120, 80, 40)]), 9);
    }

    [Fact]
    public void ColorPass_PullsAWarmCloseUpTowardsThePanel_OutsideTheCutOuts()
    {
        using var panel = Gradient(256, 256, warm: false);
        using var panelImage = SKImage.FromBitmap(panel);
        using var close = Gradient(200, 200, warm: true);
        var before = close.GetPixel(100, 100);

        var result = HybridZoomColorPass.Apply(close, panelImage, ViewRect.Create(0, 0, 1, 1)!.Value, [[(0.0, 0.0), (0.3, 0.0), (0.3, 0.3), (0.0, 0.3)]]);

        Assert.NotNull(result.Fit);
        Assert.True(result.DeltaEAfter < result.DeltaEBefore / 3, $"{result.DeltaEBefore} → {result.DeltaEAfter}");
        var after = close.GetPixel(100, 100);
        var target = panel.GetPixel(128, 128);
        Assert.True(Math.Abs(after.Red - target.Red) < Math.Abs(before.Red - target.Red));
        Assert.True(result.Samples < HybridZoomColorPass.Grid * HybridZoomColorPass.Grid);
    }

    /// <summary>A smooth two-way gradient; warm = red lifted, blue cut (the capture photos' cast).</summary>
    private static SKBitmap Gradient(int w, int h, bool warm)
    {
        var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                double r = 40 + (160.0 * x / w), g = 40 + (160.0 * y / h), b = 60 + (100.0 * (x + y) / (w + h));
                if (warm)
                {
                    (r, b) = ((r * 1.12) + 6, (b * 0.82) - 4);
                }

                bitmap.SetPixel(x, y, new SKColor((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255)));
            }
        }

        return bitmap;
    }
}
