using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// The working window for one hold: a crop of the full photo around the seed, downscaled so the seed
/// radius is at most <see cref="MaxWorkingRadius"/> pixels (a 500 px volume and a 30 px crimp then cost the
/// same), plus the seed geometry expressed in the crop's working pixels.
/// </summary>
internal sealed class OutlineCrop : IDisposable
{
    /// <summary>Largest seed radius, in working pixels, the segmenters ever see.</summary>
    public const double MaxWorkingRadius = 60;

    /// <summary>Crop half-size in seed radii: the background ring lives between 1.3 r and 1.9 r.</summary>
    public const double HalfSizeInRadii = 2.0;

    private OutlineCrop(Mat bgr, Rect roi, double scale, Point2d centre, double radius, double rx, double ry, bool hasBox)
    {
        HasBox = hasBox;
        Bgr = bgr;
        Roi = roi;
        Scale = scale;
        Centre = centre;
        Radius = radius;
        RadiusX = rx;
        RadiusY = ry;
    }

    /// <summary>Gets the working BGR pixels (owned).</summary>
    public Mat Bgr { get; }

    /// <summary>Gets the crop rectangle in full-image pixels.</summary>
    public Rect Roi { get; }

    /// <summary>Gets working pixels per full-image pixel (≤ 1).</summary>
    public double Scale { get; }

    /// <summary>Gets the seed centre in working pixels.</summary>
    public Point2d Centre { get; }

    /// <summary>Gets the seed radius in working pixels (half the box's longer side).</summary>
    public double Radius { get; }

    /// <summary>Gets the seed box half-width in working pixels (= <see cref="Radius"/> without a box).</summary>
    public double RadiusX { get; }

    /// <summary>Gets the seed box half-height in working pixels.</summary>
    public double RadiusY { get; }

    /// <summary>Gets a value indicating whether the seed carried a detector box (not just a radius).</summary>
    public bool HasBox { get; }

    /// <summary>Gets the seed's nominal area (the inscribed ellipse of its box) in working pixels.</summary>
    public double SeedArea => Math.PI * RadiusX * RadiusY;

    /// <summary>Cuts and scales the working window for a seed.</summary>
    /// <param name="image">The full BGR photo.</param>
    /// <param name="seed">The hold seed.</param>
    /// <returns>The crop, or null when the seed lies off the image.</returns>
    public static OutlineCrop? Create(Mat image, HoldSeed seed)
    {
        int w = image.Width, h = image.Height;
        double cxFull = seed.X * w, cyFull = seed.Y * h;
        double rFull = Math.Max(seed.Radius * Math.Max(w, h), 4);
        bool hasBox = seed.BoxWidth is > 0 && seed.BoxHeight is > 0;
        double rxFull = hasBox ? seed.BoxWidth!.Value * w / 2 : rFull;
        double ryFull = hasBox ? seed.BoxHeight!.Value * h / 2 : rFull;
        rFull = Math.Max(Math.Max(rxFull, ryFull), 4);
        if (cxFull < 0 || cyFull < 0 || cxFull >= w || cyFull >= h)
        {
            return null;
        }

        int half = (int)Math.Ceiling((HalfSizeInRadii * rFull) + 3);
        int x0 = Math.Max(0, (int)cxFull - half), y0 = Math.Max(0, (int)cyFull - half);
        int x1 = Math.Min(w, (int)cxFull + half), y1 = Math.Min(h, (int)cyFull + half);
        var roi = new Rect(x0, y0, x1 - x0, y1 - y0);
        double scale = Math.Min(1.0, MaxWorkingRadius / rFull);

        Mat work;
        using (var view = new Mat(image, roi))
        {
            work = new Mat();
            if (scale < 1.0)
            {
                Cv2.Resize(view, work, new Size(0, 0), scale, scale, InterpolationFlags.Area);
                scale = (double)work.Width / roi.Width;
            }
            else
            {
                view.CopyTo(work);
            }
        }

        var centre = new Point2d((cxFull - x0) * scale, (cyFull - y0) * scale);
        return new OutlineCrop(work, roi, scale, centre, rFull * scale, rxFull * scale, ryFull * scale, hasBox);
    }

    /// <summary>Maps a working-pixel point back to normalized full-image coordinates.</summary>
    /// <param name="x">Working X.</param>
    /// <param name="y">Working Y.</param>
    /// <param name="imageWidth">Full image width.</param>
    /// <param name="imageHeight">Full image height.</param>
    /// <returns>The normalized point.</returns>
    public NormalizedPoint ToNormalized(double x, double y, int imageWidth, int imageHeight)
    {
        return new NormalizedPoint(
            (Roi.X + (x / Scale)) / imageWidth,
            (Roi.Y + (y / Scale)) / imageHeight);
    }

    /// <inheritdoc/>
    public void Dispose() => Bgr.Dispose();
}
