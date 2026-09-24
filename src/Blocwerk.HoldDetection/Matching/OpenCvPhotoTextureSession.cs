using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>One decoded photo (gray, full size), matched against textures in turn.</summary>
internal sealed class OpenCvPhotoTextureSession : IPhotoTextureSession
{
    /// <summary>
    /// First search radius around a predicted view: the prediction carries the model's and the focal length's
    /// errors, a few centimetres; still under half the 20 cm bolt-hole pitch, so the nearest hole stays unique.
    /// </summary>
    internal const double SeededSearchRadiusMm = 90;

    private readonly Mat photo;

    public OpenCvPhotoTextureSession(Mat gray)
    {
        photo = gray;
    }

    public int Width => photo.Width;

    public int Height => photo.Height;

    public PhotoTextureMatch Match(byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed = null)
    {
        ArgumentNullException.ThrowIfNull(encodedTexture);
        if (ImagePixelLimit.IsTooLarge(encodedTexture))
        {
            return PhotoTextureMatch.Failed("the texture is too large to decode");
        }

        using var texture = Cv2.ImDecode(encodedTexture, ImreadModes.Grayscale | ImreadModes.IgnoreOrientation);
        if (texture.Empty())
        {
            return PhotoTextureMatch.Failed("the texture could not be decoded");
        }

        using var mask = DecodeMask(encodedMask, texture.Size());
        var mmPerPx = textureMmPerPx > 0 ? textureMmPerPx : 1;
        var (h, inliers, ratioMatches) = seed is { Length: 9 }
            ? (FromRowMajor(seed), 0, 0)
            : TextureCoarseMatcher.Find(photo, texture, mmPerPx);
        if (h is null)
        {
            return PhotoTextureMatch.Failed($"no overlap found ({inliers} coarse inliers of {ratioMatches} matches)", inliers);
        }

        var radius = seed is null ? TextureFineMatcher.SearchRadiusMm : SeededSearchRadiusMm;
        var pairs = TextureMatchRefiner.Match(photo, texture, mask, h, mmPerPx, radius);
        var stage = seed is null ? "after the coarse overlap" : "around the predicted view";
        return pairs.Count < 4
            ? PhotoTextureMatch.Failed($"{pairs.Count} fine matches {stage}", inliers)
            : new PhotoTextureMatch(pairs, inliers, null);
    }

    public void Dispose()
    {
        photo.Dispose();
    }

    private static double[,] FromRowMajor(double[] m)
    {
        var h = new double[3, 3];
        for (var i = 0; i < 9; i++)
        {
            h[i / 3, i % 3] = m[i];
        }

        return h;
    }

    /// <summary>The coverage mask at the texture's size, or null when there is none or it does not fit.</summary>
    private static Mat? DecodeMask(byte[]? encoded, Size size)
    {
        if (encoded is null || ImagePixelLimit.IsTooLarge(encoded))
        {
            return null;
        }

        var mask = Cv2.ImDecode(encoded, ImreadModes.Grayscale);
        if (!mask.Empty() && mask.Size() == size)
        {
            return mask;
        }

        mask.Dispose();
        return null;
    }
}
