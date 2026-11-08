using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>A hold crop file and its seed box in the crop's pixels.</summary>
/// <param name="File">Fixture file name without extension.</param>
/// <param name="Left">Box left.</param>
/// <param name="Top">Box top.</param>
/// <param name="Width">Box width.</param>
/// <param name="Height">Box height.</param>
internal sealed record HoldFixture(string File, int Left, int Top, int Width, int Height)
{
    /// <summary>Gets the box centre in pixels.</summary>
    public Point2d Centre => new(Left + (Width / 2.0), Top + (Height / 2.0));
}
