using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A matcher that "finds" exactly the correspondences its views describe, so the Core side (registration,
/// placement, the run log) is tested without OpenCV. Photos are identified by their first byte; 255 does not decode.
/// </summary>
internal sealed class FakePhotoTextureMatcher : IPhotoTextureMatcher
{
    public const int Width = 4000;
    public const int Height = 3000;

    public List<FakeTextureView> Views { get; } = [];

    public IPhotoTextureSession OpenPhoto(byte[] encodedPhoto) =>
        encodedPhoto.Length == 0 || encodedPhoto[0] == 255
            ? throw new ArgumentException("The photo could not be decoded.", nameof(encodedPhoto))
            : new FakePhotoTextureSession(this, encodedPhoto[0]);

    public PhotoTextureMatch Match(byte photo, byte texture)
    {
        var view = Views.FirstOrDefault(v => v.Photo == photo && v.Texture == texture);
        if (view is null)
        {
            return PhotoTextureMatch.Failed("no overlap found (3 coarse inliers of 20 matches)", 3);
        }

        var pairs = new List<PointCorrespondence>();
        for (var x = view.X0 + (view.Step / 2); x < view.X1; x += view.Step)
        {
            for (var y = 100.0; y < Height - 100; y += view.Step)
            {
                var (u, v) = view.PhotoToTexture.Apply(x, y);
                pairs.Add(new PointCorrespondence(x, y, u, v));
            }
        }

        return new PhotoTextureMatch(pairs, 120, null);
    }
}
