using SkiaSharp;

namespace Blocwerk.HoldDetection.Alignment;

/// <summary>
/// A single-channel 8-bit image plus the scale factor relative to the original
/// decoded photo, so recovered coordinates can be mapped back to full resolution.
/// </summary>
internal sealed class GrayImage
{
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Original-pixel = downscaled-pixel * Scale.</summary>
    public required double Scale { get; init; }

    /// <summary>Width of the source image before downscaling.</summary>
    public required int OriginalWidth { get; init; }

    /// <summary>Height of the source image before downscaling.</summary>
    public required int OriginalHeight { get; init; }

    public byte At(int x, int y) => Pixels[(y * Width) + x];

    /// <summary>
    /// Decodes image bytes and downscales so the long edge is at most
    /// <paramref name="maxEdge"/>, converting to grayscale. Returns null if the
    /// bytes cannot be decoded.
    /// </summary>
    public static GrayImage? DecodeDownscaled(byte[] imageData, int maxEdge = 1000)
    {
        using var decoded = SKBitmap.Decode(imageData);
        if (decoded == null)
        {
            return null;
        }

        var longEdge = Math.Max(decoded.Width, decoded.Height);
        var scale = longEdge > maxEdge ? (double)longEdge / maxEdge : 1.0;
        var w = Math.Max(1, (int)Math.Round(decoded.Width / scale));
        var h = Math.Max(1, (int)Math.Round(decoded.Height / scale));

        SKBitmap source = decoded;
        SKBitmap? scaled = null;
        if (w != decoded.Width || h != decoded.Height)
        {
            scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            decoded.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            source = scaled;
        }

        var pixels = source.Pixels;
        var gray = new byte[w * h];
        for (var i = 0; i < gray.Length; i++)
        {
            var c = pixels[i];
            // Integer luma (Rec. 601-ish) matching common ORB grayscale.
            gray[i] = (byte)(((19595 * c.Red) + (38470 * c.Green) + (7471 * c.Blue)) >> 16);
        }

        scaled?.Dispose();

        return new GrayImage
        {
            Pixels = gray,
            Width = w,
            Height = h,
            Scale = w == decoded.Width ? 1.0 : (double)decoded.Width / w,
            OriginalWidth = decoded.Width,
            OriginalHeight = decoded.Height,
        };
    }

    /// <summary>Returns a 3x3 box-blurred copy (applied <paramref name="passes"/> times).</summary>
    public GrayImage Blurred(int passes = 2)
    {
        var src = Pixels;
        var tmp = new byte[src.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            for (var y = 1; y < Height - 1; y++)
            {
                for (var x = 1; x < Width - 1; x++)
                {
                    var sum = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var row = (y + dy) * Width;
                        sum += src[row + x - 1] + src[row + x] + src[row + x + 1];
                    }

                    tmp[(y * Width) + x] = (byte)(sum / 9);
                }
            }

            (src, tmp) = (tmp, src);
        }

        return new GrayImage
        {
            Pixels = src,
            Width = Width,
            Height = Height,
            Scale = Scale,
            OriginalWidth = OriginalWidth,
            OriginalHeight = OriginalHeight,
        };
    }
}
