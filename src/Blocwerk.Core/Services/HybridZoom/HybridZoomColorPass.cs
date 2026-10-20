// <copyright file="HybridZoomColorPass.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.HybridZoom;
using SkiaSharp;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>What the colour match did: the fit (null when it was not applied) and the mean ΔE over the overlap.</summary>
/// <param name="Fit">The applied fit, or null.</param>
/// <param name="DeltaEBefore">Mean CIE76 ΔE between close-up and panel photo before.</param>
/// <param name="DeltaEAfter">The same after (equal to before when not applied).</param>
/// <param name="Samples">Paired samples the overlap gave.</param>
/// <param name="CastBefore">ΔE between the mean colours of the two over the overlap before: the colour jump itself.</param>
/// <param name="CastAfter">The same after.</param>
internal sealed record ColorPassResult(
    ColorFit? Fit, double DeltaEBefore, double DeltaEAfter, int Samples, double CastBefore = 0, double CastAfter = 0);

/// <summary>
/// Matches a drawn close-up's colours to the panel photo under it (<see cref="ColorMatch"/>): both are reduced to the
/// same <see cref="Grid"/>² grid over the view (an area average, so the sharper close-up and the softer panel photo are
/// compared at one resolution), the cut-outs are masked off, and the fitted per-channel tables are applied to the whole
/// close-up — only when they lower the mean ΔE. Runs before the cut-outs are cleared, while every pixel is opaque.
/// </summary>
internal static class HybridZoomColorPass
{
    /// <summary>Samples per side.</summary>
    public const int Grid = 64;

    /// <summary>The match, applied to <paramref name="target"/> in place.</summary>
    /// <param name="target">The drawn close-up (RGBA8888, opaque).</param>
    /// <param name="panel">The panel photo (upright, any size).</param>
    /// <param name="rect">The panel rectangle the close-up covers.</param>
    /// <param name="holes">The cut-outs, view coordinates.</param>
    /// <returns>What it did.</returns>
    public static ColorPassResult Apply(SKBitmap target, SKImage panel, ViewRect rect, IReadOnlyList<IReadOnlyList<(double U, double V)>> holes)
    {
        using var drawn = SKImage.FromBitmap(target);
        using var close = Shrunk(drawn, SKRect.Create(0, 0, target.Width, target.Height));
        using var under = Shrunk(
            panel,
            SKRect.Create((float)(rect.X * panel.Width), (float)(rect.Y * panel.Height), (float)(rect.W * panel.Width), (float)(rect.H * panel.Height)));
        using var mask = Mask(holes);
        var (source, sink) = Pairs(close, under, mask);
        var before = ColorMatch.MeanDeltaE(source, sink);
        var castBefore = ColorMatch.CastDeltaE(source, sink);
        var fit = ColorMatch.Fit(source, sink);
        if (fit is null)
        {
            return new ColorPassResult(null, before, before, source.Count, castBefore, castBefore);
        }

        var mapped = source.Select(fit.Apply).ToList();
        var after = ColorMatch.MeanDeltaE(mapped, sink);
        if (after >= before)
        {
            return new ColorPassResult(null, before, before, source.Count, castBefore, castBefore);
        }

        Map(target, fit);
        return new ColorPassResult(fit, before, after, source.Count, castBefore, ColorMatch.CastDeltaE(mapped, sink));
    }

    private static SKBitmap Shrunk(SKImage image, SKRect source)
    {
        var small = new SKBitmap(new SKImageInfo(Grid, Grid, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(small);
        canvas.DrawImage(image, source, SKRect.Create(0, 0, Grid, Grid), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return small;
    }

    /// <summary>White where samples count; the holes (grown a little) black.</summary>
    private static SKBitmap Mask(IReadOnlyList<IReadOnlyList<(double U, double V)>> holes)
    {
        var mask = new SKBitmap(new SKImageInfo(Grid, Grid, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(mask);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 3, IsAntialias = false };
        foreach (var hole in holes.Where(h => h.Count >= 3))
        {
            using var path = new SKPath();
            path.AddPoly([.. hole.Select(p => new SKPoint((float)(p.U * Grid), (float)(p.V * Grid)))], close: true);
            canvas.DrawPath(path, paint);
        }

        return mask;
    }

    private static (List<Rgb> Source, List<Rgb> Sink) Pairs(SKBitmap close, SKBitmap under, SKBitmap mask)
    {
        var source = new List<Rgb>(Grid * Grid);
        var sink = new List<Rgb>(Grid * Grid);
        for (var y = 0; y < Grid; y++)
        {
            for (var x = 0; x < Grid; x++)
            {
                if (mask.GetPixel(x, y).Red < 128)
                {
                    continue;
                }

                var c = close.GetPixel(x, y);
                var p = under.GetPixel(x, y);
                source.Add(new Rgb(c.Red, c.Green, c.Blue));
                sink.Add(new Rgb(p.Red, p.Green, p.Blue));
            }
        }

        return (source, sink);
    }

    /// <summary>Applies the fit's tables to every pixel (opaque, so premultiplication does not matter).</summary>
    private static void Map(SKBitmap target, ColorFit fit)
    {
        byte[] r = fit.R.Lut(), g = fit.G.Lut(), b = fit.B.Lut();
        var span = target.GetPixelSpan().ToArray();
        for (var i = 0; i + 3 < span.Length; i += 4)
        {
            span[i] = r[span[i]];
            span[i + 1] = g[span[i + 1]];
            span[i + 2] = b[span[i + 2]];
        }

        System.Runtime.InteropServices.Marshal.Copy(span, 0, target.GetPixels(), span.Length);
        target.NotifyPixelsChanged();
    }
}
