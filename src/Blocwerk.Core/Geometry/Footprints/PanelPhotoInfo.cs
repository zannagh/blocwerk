// <copyright file="PanelPhotoInfo.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>
/// What a planar camera pose needs of a hold photo besides its holds: the raw pixel size its normalised hold
/// positions refer to and, when the photo's EXIF carries a 35 mm-equivalent focal length, the focal length in px.
/// </summary>
/// <param name="Width">Raw (EXIF-unrotated) width, px.</param>
/// <param name="Height">Raw height, px.</param>
/// <param name="FocalPx">Focal length, px, or null when unknown (then self-calibrated from the homography).</param>
public sealed record PanelPhotoInfo(int Width, int Height, double? FocalPx)
{
    /// <summary>Reads the header size and EXIF focal length of an encoded photo; null when it is no image.</summary>
    /// <param name="photo">The encoded photo.</param>
    /// <returns>The info, or null.</returns>
    public static PanelPhotoInfo? FromImage(byte[] photo)
    {
        if (ImagePixelLimit.ReadSize(photo) is not { Width: > 0, Height: > 0 } size)
        {
            return null;
        }

        // 35 mm-equivalent focal length ÷ the 36 mm frame width × the long side (as texture registration uses it).
        var exif = ExifCameraReader.Read(photo);
        if (exif.Focal35mm is null && MetadataOnly(photo) is { } header)
        {
            exif = ExifCameraReader.Read(header);
        }

        var focal = exif.Focal35mm is { } f35 && f35 > 0
            ? f35 / 36.0 * Math.Max(size.Width, size.Height)
            : (double?)null;
        return new PanelPhotoInfo(size.Width, size.Height, focal);
    }

    /// <summary>
    /// A JPEG cut short (only its header read, <see cref="Services.PanelPhotoInfoLoader"/>) as a complete, pixel-less
    /// JPEG: its marker segments up to the first scan, then end-of-image, so the EXIF reader's full walk accepts it.
    /// </summary>
    private static byte[]? MetadataOnly(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            if (jpeg[pos + 1] == 0xDA)
            {
                return [.. jpeg.AsSpan(0, pos), 0xFF, 0xD9];
            }

            pos += 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
        }

        return null;
    }
}
