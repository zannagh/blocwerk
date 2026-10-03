using SkiaSharp;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// A decompression-bomb guard: the pixel count an image claims in its HEADER, read without decoding
/// any pixels, checked before a full decode (OpenCV <c>ImDecode</c>, SkiaSharp) allocates for it. A
/// 30 MB JPEG can claim 65 535 × 65 535 pixels, i.e. ~12 GB of BGR buffer.
/// </summary>
public static class ImagePixelLimit
{
    /// <summary>Largest accepted image: 100 megapixels (a 200 MP phone mode is refused; 48 MP passes).</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>The width and height from the header, or null when the bytes are no image SkiaSharp knows.</summary>
    /// <param name="image">The encoded image.</param>
    /// <returns>The raw (EXIF-unrotated) size, or null.</returns>
    public static (int Width, int Height)? ReadSize(byte[] image)
    {
        try
        {
            using var data = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(data);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True when the header claims more than <see cref="MaxPixels"/>. Unknown sizes pass.</summary>
    /// <param name="image">The encoded image.</param>
    /// <returns>Whether the image is too large to decode.</returns>
    public static bool IsTooLarge(byte[] image) =>
        ReadSize(image) is { } size && IsTooLarge(size.Width, size.Height);

    /// <summary>True when <paramref name="width"/> × <paramref name="height"/> exceeds <see cref="MaxPixels"/>.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>Whether the size is too large.</returns>
    public static bool IsTooLarge(int width, int height) => (long)width * height > MaxPixels;

    /// <summary>Throws <see cref="ArgumentException"/> when the header claims too many pixels.</summary>
    /// <param name="image">The encoded image.</param>
    /// <param name="paramName">The caller's parameter name.</param>
    public static void EnsureDecodable(byte[] image, string paramName)
    {
        if (IsTooLarge(image))
        {
            throw new ArgumentException(
                $"The image is larger than {MaxPixels / 1_000_000} megapixels and is not decoded.", paramName);
        }
    }
}
