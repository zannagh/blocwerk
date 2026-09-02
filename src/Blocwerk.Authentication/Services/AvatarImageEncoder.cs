using SkiaSharp;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The avatar pipeline: decode, scale the longest edge down to <see cref="MaxEdge"/>, re-encode.
/// </summary>
/// <remarks>
/// Lifted out of <see cref="CurrentUserService"/> unchanged so maintenance can push a legacy avatar
/// through the SAME code a fresh upload goes through, rather than through a second implementation
/// that would drift from it. <see cref="CurrentUserService.SetAvatarAsync"/> still calls exactly
/// this, so upload behaviour is untouched.
/// </remarks>
public static class AvatarImageEncoder
{
    /// <summary>Longest edge of a stored avatar, in pixels.</summary>
    public const int MaxEdge = 512;

    /// <summary>Encoder quality for the stored avatar; visually indistinguishable at 512 px.</summary>
    public const int Quality = 80;

    /// <summary>
    /// Decodes <paramref name="image"/>, scales it so its longest edge is at most
    /// <see cref="MaxEdge"/> (preserving aspect ratio; smaller images are left at their pixel size),
    /// and re-encodes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bytes cannot be decoded or re-encoded, so
    /// the UI can show its avatar error.</exception>
    public static (byte[] Image, string ContentType) Scale(byte[] image)
    {
        // Skia throws rather than returning null when it cannot build a codec for the bytes, so the
        // friendly error below needs the catch to fire at all.
        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(image);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            decoded = null;
        }

        if (decoded == null)
        {
            throw new InvalidOperationException("Couldn't read that image. Please try a JPEG, PNG or WebP file.");
        }

        using (decoded)
        {
            return Encode(decoded);
        }
    }

    /// <summary>
    /// The pixel dimensions the stored bytes declare, without decoding them, or null when they are
    /// not a readable image. Used only to report what a stored avatar currently is.
    /// </summary>
    public static (int Width, int Height)? Measure(byte[] image)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(image));
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // WebP, not PNG. An avatar is a photograph, and PNG is a lossless codec for it: scaled 512px
    // avatars still came out at 300-900 kB, which is then rendered beside every name on a page.
    // WebP keeps the alpha channel a cropped avatar may carry and lands the same image around
    // 20-60 kB. JPEG is the fallback for a Skia build without the WebP encoder (Encode returns
    // null), which costs the alpha channel but is still an order of magnitude off the PNG.
    private static (byte[] Image, string ContentType) Encode(SKBitmap decoded)
    {
        var longEdge = Math.Max(decoded.Width, decoded.Height);
        using SKBitmap? scaled = longEdge > MaxEdge ? ScaleToLongEdge(decoded, MaxEdge) : null;

        using var pixmapImage = SKImage.FromBitmap(scaled ?? decoded);

        using var webp = pixmapImage.Encode(SKEncodedImageFormat.Webp, Quality);
        if (webp is not null)
        {
            return (webp.ToArray(), "image/webp");
        }

        using var jpeg = pixmapImage.Encode(SKEncodedImageFormat.Jpeg, Quality);
        if (jpeg is not null)
        {
            return (jpeg.ToArray(), "image/jpeg");
        }

        throw new InvalidOperationException("Couldn't re-encode that image. Please try a JPEG, PNG or WebP file.");
    }

    private static SKBitmap ScaleToLongEdge(SKBitmap decoded, int maxEdge)
    {
        var scale = (double)maxEdge / Math.Max(decoded.Width, decoded.Height);
        var w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        var h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        var scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        decoded.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return scaled;
    }
}
