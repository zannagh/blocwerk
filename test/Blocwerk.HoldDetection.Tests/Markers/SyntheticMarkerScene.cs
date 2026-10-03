using Blocwerk.Core.Geometry;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>A marker placed on a synthetic plane: its BL corner at (A, B) mm, side SizeMm.</summary>
internal readonly record struct PlacedMarker(int Id, double A, double B, double SizeMm = 125.0);

/// <summary>
/// Renders a flat, white plane with DICT_4X4_50 markers at known mm positions, then photographs
/// it through a known homography. Plane frame as in the geometry contract: a right, b up.
/// </summary>
internal sealed class SyntheticMarkerScene : IDisposable
{
    private SyntheticMarkerScene(Mat image, PlaneHomography planeToImage)
    {
        Image = image;
        PlaneToImage = planeToImage;
    }

    /// <summary>The rendered photo (8-bit grayscale).</summary>
    public Mat Image { get; }

    /// <summary>Ground truth: plane mm → photo px.</summary>
    public PlaneHomography PlaneToImage { get; }

    /// <summary>Plane corners TL, TR, BR, BL of a marker, as the geometry document stores them.</summary>
    public static double[][] PlaneCorners(PlacedMarker m) =>
        [[m.A, m.B + m.SizeMm], [m.A + m.SizeMm, m.B + m.SizeMm], [m.A + m.SizeMm, m.B], [m.A, m.B]];

    /// <summary>
    /// Renders the plane at 1 px/mm and warps its corners (TL, TR, BR, BL of the plane as seen)
    /// onto <paramref name="photoCorners"/> in a photo of <paramref name="photoSize"/>.
    /// </summary>
    public static SyntheticMarkerScene Create(
        double widthMm,
        double heightMm,
        IEnumerable<PlacedMarker> markers,
        Point2f[] photoCorners,
        Size photoSize)
    {
        using var canvas = new Mat(new Size((int)widthMm, (int)heightMm), MatType.CV_8UC1, Scalar.All(255));
        foreach (var m in markers)
        {
            var side = (int)Math.Round(m.SizeMm);
            using var marker = ArucoInterop.GenerateMarker(m.Id, side);
            var top = (int)Math.Round(heightMm - m.B - m.SizeMm);
            using var roi = new Mat(canvas, new Rect((int)Math.Round(m.A), top, side, side));
            marker.CopyTo(roi);
        }

        Point2f[] canvasCorners = [new(0, 0), new((float)widthMm, 0), new((float)widthMm, (float)heightMm), new(0, (float)heightMm)];
        using var warp = Cv2.GetPerspectiveTransform(canvasCorners, photoCorners);
        var photo = new Mat();
        Cv2.WarpPerspective(canvas, photo, warp, photoSize, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));

        // Truth = warp ∘ (plane mm → canvas px). Canvas y = height - b, and pixel centres sit on
        // integer coordinates, so a marker's outer edge (pixel boundary) is 0.5 px before it.
        var w = new double[9];
        for (var i = 0; i < 9; i++)
        {
            w[i] = warp.At<double>(i / 3, i % 3);
        }

        var planeToCanvas = PlaneHomography.FromCoefficients([1, 0, -0.5, 0, -1, heightMm - 0.5, 0, 0, 1]);
        return new SyntheticMarkerScene(photo, Compose(w, planeToCanvas.Coefficients));
    }

    public void Dispose() => Image.Dispose();

    private static PlaneHomography Compose(double[] a, double[] b)
    {
        var r = new double[9];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                r[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            }
        }

        return PlaneHomography.FromCoefficients(r);
    }
}
