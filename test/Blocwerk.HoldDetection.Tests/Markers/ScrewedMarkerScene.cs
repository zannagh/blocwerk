using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>
/// One DICT_4X4_50 marker as it hangs on the wall: printed on white paper (quiet zone), on a
/// mid-grey wall, with dark screw heads overlapping its corners, photographed at an angle
/// (perspective), rendered 4× supersampled and area-downsampled, then blurred. The exact image
/// position of the black square's outer corners is known.
/// </summary>
internal sealed class ScrewedMarkerScene : IDisposable
{
    private const int Supersample = 4;

    private ScrewedMarkerScene(Mat image, Point2d[] truth)
    {
        Image = image;
        TruthCorners = truth;
    }

    /// <summary>The photo, 8-bit grayscale.</summary>
    public Mat Image { get; }

    /// <summary>Ground-truth outer corners of the black square, TL, TR, BR, BL, in photo pixels.</summary>
    public Point2d[] TruthCorners { get; }

    /// <summary>
    /// Renders the scene. <paramref name="photoCorners"/> are where the canvas corners (TL, TR, BR, BL)
    /// land in the photo; the marker is 120 px on a 300 px canvas.
    /// </summary>
    public static ScrewedMarkerScene Create(Point2f[] photoCorners, Size photoSize, double blurSigma = 1.2)
    {
        const int canvasSide = 300;
        const int markerSide = 120;
        const int markerOrigin = 90;
        using var canvas = RenderCanvas(canvasSide, markerSide, markerOrigin);

        Point2f[] from = [new(0, 0), new(canvasSide, 0), new(canvasSide, canvasSide), new(0, canvasSide)];
        var to = photoCorners.Select(p => new Point2f(p.X * Supersample, p.Y * Supersample)).ToArray();
        using var warp = Cv2.GetPerspectiveTransform(from, to);
        using var big = new Mat();
        var bigSize = new Size(photoSize.Width * Supersample, photoSize.Height * Supersample);
        Cv2.WarpPerspective(canvas, big, warp, bigSize, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(110));

        var photo = new Mat();
        Cv2.Resize(big, photo, photoSize, 0, 0, InterpolationFlags.Area);
        Cv2.GaussianBlur(photo, photo, new Size(0, 0), blurSigma);

        // Pixel centres are on integers, so the square's outer boundary is 0.5 px before its first pixel.
        var lo = markerOrigin - 0.5;
        var hi = markerOrigin + markerSide - 0.5;
        Point2d[] canvasTruth = [new(lo, lo), new(hi, lo), new(hi, hi), new(lo, hi)];
        var truth = canvasTruth.Select(p => ToPhoto(warp, p)).ToArray();
        return new ScrewedMarkerScene(photo, truth);
    }

    public void Dispose() => Image.Dispose();

    private static Mat RenderCanvas(int canvasSide, int markerSide, int markerOrigin)
    {
        var canvas = new Mat(new Size(canvasSide, canvasSide), MatType.CV_8UC1, Scalar.All(110));

        // White paper, a 20 px quiet zone around the black square.
        var paper = new Rect(markerOrigin - 20, markerOrigin - 20, markerSide + 40, markerSide + 40);
        canvas.Rectangle(paper, Scalar.All(235), -1);

        using var marker = ArucoInterop.GenerateMarker(7, markerSide);
        using (var roi = new Mat(canvas, new Rect(markerOrigin, markerOrigin, markerSide, markerSide)))
        {
            marker.CopyTo(roi);
        }

        // Screw heads: dark discs centred just outside each corner, overlapping it.
        var lo = markerOrigin - 4;
        var hi = markerOrigin + markerSide + 3;
        foreach (var (x, y) in new[] { (lo, lo), (hi, lo), (hi, hi), (lo, hi) })
        {
            canvas.Circle(new Point(x, y), 9, Scalar.All(45), -1, LineTypes.AntiAlias);
        }

        return canvas;
    }

    private static Point2d ToPhoto(Mat warp, Point2d p)
    {
        double H(int r, int c) => warp.At<double>(r, c);
        var w = (H(2, 0) * p.X) + (H(2, 1) * p.Y) + H(2, 2);
        var x = ((H(0, 0) * p.X) + (H(0, 1) * p.Y) + H(0, 2)) / w;
        var y = ((H(1, 0) * p.X) + (H(1, 1) * p.Y) + H(1, 2)) / w;

        // Supersampled pixel centre (x) maps to photo pixel centre ((x + 0.5) / s - 0.5).
        return new Point2d(((x + 0.5) / Supersample) - 0.5, ((y + 0.5) / Supersample) - 0.5);
    }
}
