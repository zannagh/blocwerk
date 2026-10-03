// <copyright file="PanelCropImage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using SkiaSharp;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>A cropped photo: its bytes, content type, and the rectangle actually cut (snapped to whole pixels).</summary>
/// <param name="Photo">The encoded crop.</param>
/// <param name="ContentType">Its content type.</param>
/// <param name="Rect">The crop that was cut, normalized to the source photo, on its pixel grid.</param>
public sealed record PanelCroppedPhoto(byte[] Photo, string ContentType, PanelCropRect Rect);

/// <summary>
/// Cuts a rectangle out of a stored photo. The rectangle is given in the DISPLAYED frame (the photo as the browser turns
/// it by its EXIF orientation: what the editor shows the holds and the crop box over); it is cut from the RAW pixel grid
/// (<see cref="PanelCropOrientation"/>) and the source's orientation is written back, so the browser turns the crop
/// exactly as it turned the original. PNG stays PNG; everything else becomes a high-quality JPEG.
/// </summary>
public static class PanelCropImage
{
    /// <summary>JPEG quality of the re-encoded crop: as high as the variants, hold edges matter.</summary>
    public const int Quality = 92;

    /// <summary>Crops <paramref name="source"/> to <paramref name="rect"/>.</summary>
    /// <param name="source">The photo bytes.</param>
    /// <param name="rect">The crop, normalized to the displayed photo.</param>
    /// <returns>The cropped photo; its rectangle is in the displayed frame, snapped to the raw pixels.</returns>
    /// <exception cref="InvalidOperationException">The photo does not decode.</exception>
    public static PanelCroppedPhoto Crop(byte[] source, PanelCropRect rect)
    {
        var origin = OriginOf(source);
        using var bitmap = SKBitmap.Decode(source)
            ?? throw new InvalidOperationException("The panel photo could not be read, so it cannot be cropped.");
        var pixels = Snap(PanelCropOrientation.ToRaw(rect, origin), bitmap.Width, bitmap.Height);
        using var subset = new SKBitmap();
        if (!bitmap.ExtractSubset(subset, pixels))
        {
            throw new InvalidOperationException("The crop rectangle does not fit the photo.");
        }

        var png = CapturePhotoFormat.Sniff(source) == CapturePhotoKind.Png;
        using var image = SKImage.FromBitmap(subset);
        using var data = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, Quality);
        var encoded = StoredPhotoSanitizer.WithOrientationOf(source, data.ToArray());
        var displayed = PanelCropOrientation.ToDisplayed(Normalize(pixels, bitmap.Width, bitmap.Height), origin);
        return new PanelCroppedPhoto(encoded, png ? "image/png" : "image/jpeg", displayed);
    }

    /// <summary>
    /// The rectangle <see cref="Crop"/> would actually cut (snapped to whole pixels), read from the photo's header only.
    /// </summary>
    /// <param name="source">The photo bytes.</param>
    /// <param name="rect">The requested crop.</param>
    /// <returns>The snapped crop.</returns>
    /// <exception cref="InvalidOperationException">The photo does not decode.</exception>
    public static PanelCropRect Snapped(byte[] source, PanelCropRect rect)
    {
        using var stream = new SKMemoryStream(source);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidOperationException("The panel photo could not be read, so it cannot be cropped.");
        var (width, height, origin) = (codec.Info.Width, codec.Info.Height, codec.EncodedOrigin);
        var raw = Normalize(Snap(PanelCropOrientation.ToRaw(rect, origin), width, height), width, height);
        return PanelCropOrientation.ToDisplayed(raw, origin);
    }

    /// <summary>The EXIF orientation the photo declares (upright when none or unreadable).</summary>
    private static SKEncodedOrigin OriginOf(byte[] source)
    {
        using var stream = new SKMemoryStream(source);
        using var codec = SKCodec.Create(stream);
        return codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft;
    }

    private static PanelCropRect Normalize(SKRectI pixels, int width, int height) => new(
        (double)pixels.Left / width,
        (double)pixels.Top / height,
        (double)pixels.Width / width,
        (double)pixels.Height / height);

    /// <summary>The rectangle on whole pixels, at least one pixel, inside the image.</summary>
    private static SKRectI Snap(PanelCropRect rect, int width, int height)
    {
        var left = Math.Clamp((int)Math.Round(rect.Left * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Round(rect.Top * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Round(rect.Right * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Round(rect.Bottom * height), top + 1, height);
        return new SKRectI(left, top, right, bottom);
    }
}
