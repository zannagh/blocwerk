// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;
using SkiaSharp;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Checks a gallery upload before it is stored: its bytes are the format it was declared as (a HEIC or TIFF sent as
/// <c>image/jpeg</c> would pass <see cref="StoredPhotoSanitizer"/> unchanged, location and all), and its header names a
/// picture of at most <see cref="ImagePixelLimit.MaxPixels"/>, because the resized variants decode it whole. The header is
/// read without decoding the pixels.
/// </summary>
public static class GalleryImageCheck
{
    /// <summary>
    /// Null when <paramref name="bytes"/> may be stored as <paramref name="declaredType"/>; otherwise the HTTP status
    /// (415 for a type mismatch, 400 for an unreadable or oversized picture) and the message.
    /// </summary>
    public static (int Status, string Message)? Refusal(ReadOnlySpan<byte> bytes, string declaredType)
    {
        var matches = declaredType switch
        {
            "image/jpeg" => CapturePhotoFormat.Sniff(bytes) == CapturePhotoKind.Jpeg,
            "image/png" => CapturePhotoFormat.Sniff(bytes) == CapturePhotoKind.Png,
            "image/webp" => WebpMetadataStripper.IsWebp(bytes),
            _ => false,
        };
        if (!matches)
        {
            return (415, $"The file is not a {declaredType} image.");
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return (400, "The image could not be read.");
        }

        return ImagePixelLimit.IsTooLarge(codec.Info.Width, codec.Info.Height)
            ? (400, $"The image has more than {ImagePixelLimit.MaxPixels / 1_000_000} megapixels.")
            : null;
    }
}
