using SkiaSharp;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Reads a photo's EXIF orientation tag. The whole hold pipeline (YOLO via SkiaSharp, and the OpenCV
/// outline/marker decoders with <c>IgnoreOrientation</c>) works in the RAW pixel grid and ignores this
/// tag, so a photo with orientation ≠ 1 is stored and annotated sideways relative to how a viewer that
/// honours EXIF would show it.
/// </summary>
public static class ExifOrientation
{
    /// <summary>The "no rotation" orientation (EXIF TopLeft).</summary>
    public const int Normal = 1;

    /// <summary>Reads the EXIF orientation (1..8), or null when the bytes are not a decodable image.</summary>
    /// <param name="image">The encoded image.</param>
    /// <returns>The orientation, 1 when the image carries none.</returns>
    public static int? Read(byte[] image)
    {
        try
        {
            using var data = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(data);
            return codec is null ? null : (int)codec.EncodedOrigin;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
