// <copyright file="HoldPhotoPixels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using SkiaSharp;

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// A panel photo decoded once, upright (hold positions are fractions of the photo as it is shown, so the EXIF
/// orientation is applied), for reading single pixels.
/// </summary>
internal sealed class HoldPhotoPixels : IDisposable
{
    private readonly SKBitmap bitmap;

    private HoldPhotoPixels(SKBitmap bitmap)
    {
        this.bitmap = bitmap;
    }

    /// <summary>Photo width in pixels.</summary>
    public int Width => bitmap.Width;

    /// <summary>Photo height in pixels.</summary>
    public int Height => bitmap.Height;

    /// <summary>Decodes a photo, or returns null when it is not an image Skia reads.</summary>
    /// <param name="photo">The encoded photo.</param>
    /// <returns>The pixels, or null.</returns>
    public static HoldPhotoPixels? TryOpen(byte[]? photo)
    {
        if (photo is not { Length: > 0 })
        {
            return null;
        }

        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(photo);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            decoded = null;
        }

        if (decoded is null)
        {
            return null;
        }

        var upright = ImageRendition.Reorient(decoded, ImageRendition.OriginOf(photo));
        if (upright is null)
        {
            return new HoldPhotoPixels(decoded);
        }

        decoded.Dispose();
        return new HoldPhotoPixels(upright);
    }

    /// <summary>The pixel at (x, y) in Oklab.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The colour.</returns>
    public OklabColor At(int x, int y)
    {
        var pixel = bitmap.GetPixel(x, y);
        return OklabColor.FromRgb(pixel.Red, pixel.Green, pixel.Blue);
    }

    /// <inheritdoc/>
    public void Dispose() => bitmap.Dispose();
}
