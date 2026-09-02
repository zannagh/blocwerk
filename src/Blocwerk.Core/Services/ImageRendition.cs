using SkiaSharp;

namespace Blocwerk.Core.Services;

/// <summary>
/// Turns a stored original into one downscaled rendition. Pure image work, no I/O.
/// </summary>
/// <remarks>
/// Two properties of the source have to survive the round trip, because a rendition is swapped in
/// underneath the user in place of the original and any difference shows up as a visible glitch:
/// <list type="bullet">
/// <item>
/// EXIF orientation. <see cref="SKBitmap.Decode(byte[])"/> hands back the raw pixel grid and drops
/// the tag, while browsers rotate the original according to it — so a phone photo taken sideways
/// would flip the moment the page upgraded to a rendition. The rotation is therefore BAKED IN
/// here, which is also what makes the rendition's own aspect ratio agree with the
/// <c>naturalWidth</c>/<c>naturalHeight</c> the browser reports for the original.
/// </item>
/// <item>
/// Transparency. The stitcher uploads PNGs from a canvas that is never background-filled, and JPEG
/// has no alpha channel — encoding one as JPEG flattens every transparent pixel to black. A source
/// that declares alpha is encoded as WebP instead, which keeps it.
/// </item>
/// </list>
/// </remarks>
public static class ImageRendition
{
    /// <summary>A rendition's encoded bytes and what they are encoded as.</summary>
    /// <param name="Bytes">The encoded rendition.</param>
    /// <param name="ContentType">Content type for the response.</param>
    /// <param name="Extension">File extension for the cache file, including the dot.</param>
    public sealed record Encoded(byte[] Bytes, string ContentType, string Extension);

    /// <summary>Cache file extensions a rendition can be written under.</summary>
    public static readonly string[] Extensions = [".jpg", ".webp"];

    /// <summary>
    /// The rendition of <paramref name="original"/> at <paramref name="width"/> display pixels, or
    /// null when the source is already that narrow (never upscale — it costs bytes and gains
    /// nothing) or is not a decodable image. Width is compared AFTER orientation is applied, since
    /// that is the width the browser lays the original out at.
    /// </summary>
    public static Encoded? Render(byte[] original, int width)
    {
        var origin = OriginOf(original);

        using var decoded = TryDecode(original);
        if (decoded is null)
        {
            return null;
        }

        using var upright = Reorient(decoded, origin);
        var source = upright ?? decoded;
        if (source.Width <= width)
        {
            return null;
        }

        var height = Math.Max(1, (int)Math.Round(source.Height * ((double)width / source.Width)));
        using var scaled = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        using var image = SKImage.FromBitmap(scaled);

        // WebP only where it is actually needed. It is the one format here that carries alpha, but
        // JPEG remains the better-understood choice for the opaque wall photography that is the
        // overwhelming majority of these images.
        var format = HasAlpha(original) ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Jpeg;
        using var data = image.Encode(format, ImageVariants.Quality);
        if (data is null)
        {
            return null;
        }

        return format == SKEncodedImageFormat.Webp
            ? new Encoded(data.ToArray(), "image/webp", ".webp")
            : new Encoded(data.ToArray(), "image/jpeg", ".jpg");
    }

    /// <summary>
    /// The bitmap the EXIF tag says the photographer saw, or null when the pixels are already
    /// upright and nothing has to be copied.
    /// </summary>
    private static SKBitmap? Reorient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        // Origins 5-8 are the quarter turns, which exchange the two axes.
        var quarterTurn = origin >= SKEncodedOrigin.LeftTop;
        var w = quarterTurn ? source.Height : source.Width;
        var h = quarterTurn ? source.Width : source.Height;

        var target = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(MatrixFor(origin, source.Width, source.Height));
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return target;
    }

    /// <summary>
    /// Maps a source pixel to where the EXIF origin says it belongs. <c>sw</c>/<c>sh</c> are the
    /// SOURCE dimensions, which is why the quarter turns translate by the opposite one.
    /// </summary>
    private static SKMatrix MatrixFor(SKEncodedOrigin origin, int sw, int sh) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, sw, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, sw, 0, -1, sh, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, sh, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, sh, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, sh, -1, 0, sw, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, sw, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    /// <summary>The EXIF orientation the file declares, or upright when it declares none.</summary>
    private static SKEncodedOrigin OriginOf(byte[] original)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(original));
            return codec?.EncodedOrigin ?? SKEncodedOrigin.TopLeft;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return SKEncodedOrigin.TopLeft;
        }
    }

    /// <summary>
    /// Whether the source declares an alpha channel. Read off the codec rather than by scanning
    /// pixels: a PNG that happens to be fully opaque only costs a slightly larger WebP, whereas
    /// missing a genuinely transparent one turns the stitcher's uploads black.
    /// </summary>
    private static bool HasAlpha(byte[] original)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(original));
            return codec is not null && codec.Info.AlphaType != SKAlphaType.Opaque;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Skia does not return null for undecodable input — it fails to build a codec and throws — and
    /// a corrupt stored image must not take the request down.
    /// </summary>
    private static SKBitmap? TryDecode(byte[] image)
    {
        try
        {
            return SKBitmap.Decode(image);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
