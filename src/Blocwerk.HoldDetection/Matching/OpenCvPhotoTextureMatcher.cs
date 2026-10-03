using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// <see cref="IPhotoTextureMatcher"/> with OpenCV: a coarse whole-image homography (<see cref="HomographyHelper.Coarse"/>,
/// AKAZE + RANSAC) on downscaled copies finds where the photo sits on the texture, then
/// <see cref="TextureFineMatcher"/> matches again at ~2 mm/px with the photo warped into the texture's frame.
/// Stateless, so a singleton.
/// </summary>
public sealed class OpenCvPhotoTextureMatcher : IPhotoTextureMatcher
{
    /// <inheritdoc/>
    public IPhotoTextureSession OpenPhoto(byte[] encodedPhoto)
    {
        ArgumentNullException.ThrowIfNull(encodedPhoto);
        ImagePixelLimit.EnsureDecodable(encodedPhoto, nameof(encodedPhoto));

        // IgnoreOrientation: hold X/Y are normalised to the photo as stored, without its EXIF rotation.
        var gray = Cv2.ImDecode(encodedPhoto, ImreadModes.Grayscale | ImreadModes.IgnoreOrientation);
        if (gray.Empty())
        {
            gray.Dispose();
            throw new ArgumentException("The photo could not be decoded.", nameof(encodedPhoto));
        }

        return new OpenCvPhotoTextureSession(gray);
    }
}
