namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Where the outliner should look for one hold, in the SAME conventions as <see cref="DetectedHold"/>:
/// <see cref="X"/>/<see cref="Y"/> are the centre as fractions of the image width/height, and
/// <see cref="Radius"/> is normalized by the image's LONGER side
/// (<c>max(boxW, boxH) / (2 * max(imageW, imageH))</c>, exactly what the YOLO client emits).
/// </summary>
/// <param name="X">Centre X as a fraction of the image width (0..1).</param>
/// <param name="Y">Centre Y as a fraction of the image height (0..1).</param>
/// <param name="Radius">Half the box's longer side, as a fraction of the image's longer side.</param>
/// <param name="BoxWidth">Optional detector box width as a fraction of the image width. When both box
/// sides are given the outliner uses the box instead of the radius square.</param>
/// <param name="BoxHeight">Optional detector box height as a fraction of the image height.</param>
public sealed record HoldSeed(
    double X,
    double Y,
    double Radius,
    double? BoxWidth = null,
    double? BoxHeight = null)
{
    /// <summary>Builds a seed from a detector result (centre + radius, no box).</summary>
    /// <param name="hold">The detected hold.</param>
    /// <returns>The equivalent seed.</returns>
    public static HoldSeed FromDetected(DetectedHold hold) => new(hold.X, hold.Y, hold.Radius);

    /// <summary>
    /// Builds a seed from a detector box given in pixels, deriving the radius with the YOLO convention.
    /// </summary>
    /// <param name="left">Box left edge in pixels.</param>
    /// <param name="top">Box top edge in pixels.</param>
    /// <param name="width">Box width in pixels.</param>
    /// <param name="height">Box height in pixels.</param>
    /// <param name="imageWidth">Image width in pixels.</param>
    /// <param name="imageHeight">Image height in pixels.</param>
    /// <returns>The seed.</returns>
    public static HoldSeed FromPixelBox(double left, double top, double width, double height, int imageWidth, int imageHeight)
    {
        double longSide = Math.Max(imageWidth, imageHeight);
        return new HoldSeed(
            (left + (width / 2.0)) / imageWidth,
            (top + (height / 2.0)) / imageHeight,
            Math.Max(width, height) / (2.0 * longSide),
            width / imageWidth,
            height / imageHeight);
    }
}
