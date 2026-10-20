// <copyright file="HybridZoomRenderer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.HybridZoom;
using SkiaSharp;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>A rendered close-up and its colour match.</summary>
/// <param name="Image">The encoded image.</param>
/// <param name="Color">What the colour match did, or null when it did not run.</param>
internal sealed record RenderedCloseUp(byte[] Image, ColorPassResult? Color);

/// <summary>
/// Draws a planned close-up: the capture photo, decoded only as large as the output needs (JPEG's 1/2, 1/4, 1/8
/// scaled decode), warped by the plan's homography into an image that lies exactly over the panel rectangle.
/// The photo's raw pixel grid is used (no EXIF rotation), the grid its solved camera refers to.
/// </summary>
internal static class HybridZoomRenderer
{
    /// <summary>Smallest output side, px.</summary>
    public const int MinSide = 16;

    /// <summary>Soft edge of a cut-out volume, as a share of the output's long side.</summary>
    private const float FeatherShare = 0.008f;

    /// <summary>Decoded pixels per output pixel kept as headroom for the warp's rotation and foreshortening.</summary>
    private const double DecodeHeadroom = 1.5;

    /// <summary>The output size for a view of <paramref name="aspect"/>: no larger than the crop's own pixels or <paramref name="maxPx"/>.</summary>
    /// <param name="choice">The chosen photo.</param>
    /// <param name="maxPx">Longest side allowed.</param>
    /// <param name="aspect">Width ÷ height of the view as displayed.</param>
    /// <returns>Width and height, px.</returns>
    public static (int Width, int Height) OutputSize(CloseUpChoice choice, int maxPx, double aspect)
    {
        var b = choice.PhotoBounds;
        var native = Math.Max(b.X1 - b.X0, b.Y1 - b.Y0);
        var longSide = (int)Math.Round(Math.Clamp(Math.Min(maxPx, native), MinSide, Math.Max(MinSide, maxPx)));
        var a = double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, 0.05, 20) : 1;
        return a >= 1
            ? (longSide, Math.Max(MinSide, (int)Math.Round(longSide / a)))
            : (Math.Max(MinSide, (int)Math.Round(longSide * a)), longSide);
    }

    /// <summary>
    /// The close-up (<see cref="ContentTypeOf"/>), or null when the photo cannot be decoded or the warp is singular.
    /// </summary>
    /// <param name="source">The stored capture photo.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="size">The output size (<see cref="OutputSize"/>).</param>
    /// <param name="quality">JPEG / WebP quality.</param>
    /// <param name="panel">The panel photo (upright) to match the colours to (<see cref="HybridZoomColorPass"/>); null leaves them.</param>
    /// <returns>The encoded image and what the colour match did.</returns>
    public static RenderedCloseUp? Render(byte[] source, ZoomPlan plan, (int Width, int Height) size, int quality, SKImage? panel = null)
    {
        using var codec = SKCodec.Create(new MemoryStream(source));
        if (codec is null || codec.Info.Width <= 0 || plan.SourceWidth <= 0)
        {
            return null;
        }

        var b = plan.Choice.PhotoBounds;
        var native = Math.Max(b.X1 - b.X0, b.Y1 - b.Y0) * codec.Info.Width / plan.SourceWidth;
        var want = native > 0 ? Math.Min(1.0, Math.Max(size.Width, size.Height) * DecodeHeadroom / native) : 1.0;
        var scaled = codec.GetScaledDimensions((float)want);
        using var photo = new SKBitmap(new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(photo.Info, photo.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var m = CloseUpWarp.DecodedToOutput(plan.Choice.ViewToPhoto, size.Width, size.Height, (double)scaled.Width / plan.SourceWidth);
        if (m is null || !m.All(double.IsFinite))
        {
            return null;
        }

        return Draw(photo, m, size, quality, plan, panel);
    }

    /// <summary>The content type of a plan's render: WebP with alpha when volumes are cut out of it, else JPEG.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The media type.</returns>
    public static string ContentTypeOf(ZoomPlan plan) => plan.Holes is { Count: > 0 } ? "image/webp" : "image/jpeg";

    private static RenderedCloseUp Draw(SKBitmap photo, double[] m, (int Width, int Height) size, int quality, ZoomPlan plan, SKImage? panel)
    {
        var holes = plan.Holes ?? [];
        ColorPassResult? color = null;
        using var target = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Black);
            canvas.SetMatrix(new SKMatrix(
                (float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7], (float)m[8]));
            using var image = SKImage.FromBitmap(photo);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.ResetMatrix();
            canvas.Flush();
            color = panel is null ? null : HybridZoomColorPass.Apply(target, panel, plan.Rect, holes);
            CutOut(canvas, holes, size);
            canvas.Flush();
        }

        using var encoded = SKImage.FromBitmap(target);
        var format = holes.Count > 0 ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Jpeg;
        using var data = encoded.Encode(format, Math.Clamp(quality, 40, 95));
        return new RenderedCloseUp(data.ToArray(), color);
    }

    /// <summary>Clears the holes with a soft edge (a blurred clear), so the panel photo blends in under them.</summary>
    private static void CutOut(SKCanvas canvas, IReadOnlyList<IReadOnlyList<(double U, double V)>> holes, (int Width, int Height) size)
    {
        if (holes.Count == 0)
        {
            return;
        }

        var feather = Math.Max(1f, Math.Max(size.Width, size.Height) * FeatherShare);
        using var paint = new SKPaint
        {
            BlendMode = SKBlendMode.Clear,
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather),
        };
        foreach (var hole in holes.Where(h => h.Count >= 3))
        {
            using var path = new SKPath();
            path.MoveTo((float)(hole[0].U * size.Width), (float)(hole[0].V * size.Height));
            foreach (var (u, v) in hole.Skip(1))
            {
                path.LineTo((float)(u * size.Width), (float)(v * size.Height));
            }

            path.Close();
            canvas.DrawPath(path, paint);
        }
    }
}
