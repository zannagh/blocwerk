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

    /// <summary>How far a seed may be off the true mapping (photo corners and centre, texture px) and still find a seed-only view.</summary>
    public const double SeedTolerancePx = 60;

    public List<FakeTextureView> Views { get; } = [];

    /// <summary>Gets the seeds the matcher was given, per (photo, texture).</summary>
    public List<(byte Photo, byte Texture)> Seeded { get; } = [];

    public IPhotoTextureSession OpenPhoto(byte[] encodedPhoto) =>
        encodedPhoto.Length == 0 || encodedPhoto[0] == 255
            ? throw new ArgumentException("The photo could not be decoded.", nameof(encodedPhoto))
            : new FakePhotoTextureSession(this, encodedPhoto[0]);

    public PhotoTextureMatch Match(byte photo, byte texture, double[]? seed = null)
    {
        if (seed is not null)
        {
            Seeded.Add((photo, texture));
        }

        var view = Views.FirstOrDefault(v => v.Photo == photo && v.Texture == texture);
        if (view is null || (view.NeedsSeed && !Close(seed, view.PhotoToTexture)))
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

    private static bool Close(double[]? seed, PlaneHomography truth)
    {
        if (seed is null)
        {
            return false;
        }

        var predicted = PlaneHomography.FromCoefficients(seed);
        (double X, double Y)[] probes = [(0, 0), (Width, 0), (0, Height), (Width, Height), (Width / 2.0, Height / 2.0)];
        return probes.All(p =>
        {
            var (px, py) = predicted.Apply(p.X, p.Y);
            var (tx, ty) = truth.Apply(p.X, p.Y);
            return Math.Sqrt(((px - tx) * (px - tx)) + ((py - ty) * (py - ty))) <= SeedTolerancePx;
        });
    }
}
