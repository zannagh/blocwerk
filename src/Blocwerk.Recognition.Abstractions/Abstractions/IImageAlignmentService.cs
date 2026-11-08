namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Estimates a projective alignment between two images entirely on the local
/// machine (no external service). Used to pre-align stitched panorama layers
/// and old-vs-new wall photos so the user only has to fine-tune the result.
/// </summary>
public interface IImageAlignmentService
{
    /// <summary>
    /// Estimates a homography mapping <paramref name="imageToAlign"/>'s pixel
    /// coordinates into <paramref name="baseImage"/>'s pixel frame.
    /// Returns null when no reliable alignment could be found.
    /// </summary>
    Task<Homography?> AlignAsync(byte[] baseImage, byte[] imageToAlign);

    /// <summary>
    /// Like <see cref="AlignAsync"/> but the homography maps normalized [0,1]
    /// coordinates of <paramref name="imageToAlign"/> to normalized [0,1]
    /// coordinates of <paramref name="baseImage"/> (for resolution-independent
    /// points such as holds). Returns null when no reliable alignment was found.
    /// </summary>
    Task<Homography?> AlignNormalizedAsync(byte[] baseImage, byte[] imageToAlign);
}

/// <summary>
/// A 3x3 projective transform. <see cref="M"/> holds the nine coefficients in
/// row-major order and is normalized so that M[8] == 1.
/// </summary>
public sealed record Homography(double[] M, int Inliers, double Confidence)
{
    /// <summary>Projects a point (x, y) through the homography.</summary>
    public (double X, double Y) Project(double x, double y)
    {
        var w = (M[6] * x) + (M[7] * y) + M[8];
        if (Math.Abs(w) < 1e-12)
        {
            w = 1e-12;
        }

        var px = ((M[0] * x) + (M[1] * y) + M[2]) / w;
        var py = ((M[3] * x) + (M[4] * y) + M[5]) / w;
        return (px, py);
    }
}
