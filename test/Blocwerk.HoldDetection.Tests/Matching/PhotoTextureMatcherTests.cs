using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.HoldDetection.Matching;
using OpenCvSharp;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Matching;

/// <summary>
/// Photo → facet texture registration end to end on synthetic images: a random, wall-like texture at 1 mm/px is
/// "photographed" through a known perspective (plus a lighting change and noise); the matcher and
/// <see cref="PhotoTextureRegistration"/> must recover the photo → plane mapping to within 5 mm.
/// </summary>
public class PhotoTextureMatcherTests(ITestOutputHelper output)
{
    private const int TextureWidth = 2400;
    private const int TextureHeight = 1400;

    // Texture corners (TL, TR, BR, BL) as they appear in the 3000 × 2000 photo: a keystoned, slightly rotated view.
    private static readonly Point2f[] PhotoCorners = [new(420, 260), new(2700, 140), new(2850, 1880), new(260, 1700)];

    private static readonly TexturePlaneFrame Frame = new("0", -200, TextureWidth - 200, 100, TextureHeight + 100, TextureWidth, TextureHeight);

    [Fact]
    public void KnownPerspective_IsRecoveredWithinFiveMillimetres()
    {
        using var texture = SyntheticTexture(seed: 7);
        var (photoBytes, photoToTexture) = Photograph(texture);
        var extent = new PlaneRectMm(Frame.AMin, Frame.AMax, Frame.BMin, Frame.BMax);

        using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photoBytes);
        var match = session.Match(texture.ToBytes(".png"), null, 1.0);
        var registration = PhotoTextureRegistration.Register(match, session.Width, session.Height, Frame, extent);

        output.WriteLine($"{registration.Matches} matches, {registration.Inliers} inliers, coverage {registration.Coverage:P0}, RMS {registration.RmsMm:F2} mm");
        Assert.True(registration.Accepted, registration.Reason);
        Assert.True(registration.Inliers >= PhotoTextureRegistration.MinInliers);

        var worst = 0.0;
        for (var tx = 150; tx < TextureWidth; tx += 300)
        {
            for (var ty = 150; ty < TextureHeight; ty += 250)
            {
                // A point of the texture, where the photo shows it, mapped back through the registration.
                var photo = Apply(Invert(photoToTexture), tx, ty);
                var (a, b) = registration.Map(photo.X / session.Width, photo.Y / session.Height);
                var (ta, tb) = Frame.ToPlane(tx, ty);
                worst = Math.Max(worst, Math.Sqrt(((a - ta) * (a - ta)) + ((b - tb) * (b - tb))));
            }
        }

        output.WriteLine($"worst error {worst:F2} mm");
        Assert.True(worst <= 5, $"worst error {worst:F2} mm");
    }

    [Fact]
    public void UnrelatedTexture_IsNotAccepted()
    {
        using var texture = SyntheticTexture(seed: 7);
        using var other = SyntheticTexture(seed: 99);
        var (photoBytes, _) = Photograph(texture);
        var extent = new PlaneRectMm(Frame.AMin, Frame.AMax, Frame.BMin, Frame.BMax);

        using var session = new OpenCvPhotoTextureMatcher().OpenPhoto(photoBytes);
        var match = session.Match(other.ToBytes(".png"), null, 1.0);
        var registration = PhotoTextureRegistration.Register(match, session.Width, session.Height, Frame, extent);

        output.WriteLine($"{registration.Matches} matches, {registration.Inliers} inliers — {registration.Reason}");
        Assert.False(registration.Accepted);
    }

    [Fact]
    public void UndecodablePhoto_Throws_AndUndecodableTexture_FailsTheMatch()
    {
        var matcher = new OpenCvPhotoTextureMatcher();
        Assert.Throws<ArgumentException>(() => matcher.OpenPhoto([1, 2, 3]));

        using var texture = SyntheticTexture(seed: 7);
        var (photoBytes, _) = Photograph(texture);
        using var session = matcher.OpenPhoto(photoBytes);
        Assert.NotNull(session.Match([1, 2, 3], null, 1.0).Failure);
    }

    /// <summary>A plywood-like background with a few thousand random blobs and bars of random tone and size.</summary>
    internal static Mat SyntheticTexture(int seed)
    {
        var rng = new Random(seed);
        var img = new Mat(TextureHeight, TextureWidth, MatType.CV_8UC3, new Scalar(150, 170, 190));
        for (var i = 0; i < 2500; i++)
        {
            var colour = new Scalar(rng.Next(256), rng.Next(256), rng.Next(256));
            var centre = new Point(rng.Next(TextureWidth), rng.Next(TextureHeight));
            if (rng.Next(2) == 0)
            {
                Cv2.Circle(img, centre, rng.Next(4, 30), colour, -1);
            }
            else
            {
                Cv2.Rectangle(img, new Rect(centre.X, centre.Y, rng.Next(4, 50), rng.Next(4, 50)), colour, -1);
            }
        }

        Cv2.GaussianBlur(img, img, new Size(3, 3), 0);
        return img;
    }

    /// <summary>The texture seen through <see cref="PhotoCorners"/>, darker and noisy, as a JPEG; and the true photo px → texture px mapping.</summary>
    private static (byte[] Jpeg, double[,] PhotoToTexture) Photograph(Mat texture)
    {
        Point2f[] src = [new(0, 0), new(TextureWidth - 1, 0), new(TextureWidth - 1, TextureHeight - 1), new(0, TextureHeight - 1)];
        using var textureToPhoto = Cv2.GetPerspectiveTransform(src, PhotoCorners);
        using var photo = new Mat();
        Cv2.WarpPerspective(texture, photo, textureToPhoto, new Size(3000, 2000), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(40, 40, 40));
        photo.ConvertTo(photo, -1, 0.8, 10);
        using var noise = new Mat(photo.Size(), photo.Type());
        Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(4, 4, 4));
        Cv2.Add(photo, noise, photo);
        var h = new double[3, 3];
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                h[r, c] = textureToPhoto.At<double>(r, c);
            }
        }

        return (photo.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 92)), Mat3.Invert(h));
    }

    private static double[,] Invert(double[,] h) => Mat3.Invert(h);

    private static Pt Apply(double[,] h, double x, double y) => HomographyHelper.Warp(h, new Pt(x, y));
}
