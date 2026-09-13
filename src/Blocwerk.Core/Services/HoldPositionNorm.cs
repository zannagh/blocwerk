namespace Blocwerk.Core.Services;

/// <summary>
/// A normalized (0..1) hold centre position in a wall image. Used to carry the matcher's
/// warp-predicted new-image position for an old hold from the review session through to promote.
/// </summary>
/// <param name="X">Normalized centre X (0..1) in the new image.</param>
/// <param name="Y">Normalized centre Y (0..1) in the new image.</param>
public sealed record HoldPositionNorm(double X, double Y);
