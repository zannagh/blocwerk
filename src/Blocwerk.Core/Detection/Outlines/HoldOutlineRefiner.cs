using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Post-processes one traced outline: smooth it (<see cref="HoldShapeSmoother"/>) or, when it cannot be made
/// plausible, hand back the circle fallback. Pure, so it is testable without OpenCV.
/// </summary>
public static class HoldOutlineRefiner
{
    /// <summary>Confidence ceiling of a circle fallback (the outliner's own convention).</summary>
    public const double CircleConfidenceCeiling = 0.2;

    /// <summary>Smooths the result's shape; a jagged one becomes a circle fallback (no shape points, low confidence).</summary>
    /// <param name="result">The raw outliner result.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The refined result (the input when it has no contour or is already smooth).</returns>
    public static HoldOutlineResult Refine(HoldOutlineResult result, double aspect)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Method == HoldOutlineMethod.CircleFallback || result.ShapePoints is not { Count: >= 3 } shape)
        {
            return result;
        }

        var smooth = HoldShapeSmoother.Smooth(shape, aspect);
        if (smooth is null)
        {
            return result with
            {
                Method = HoldOutlineMethod.CircleFallback,
                ShapePoints = null,
                ShapeHoles = null,
                Confidence = Math.Min(result.Confidence, CircleConfidenceCeiling),
            };
        }

        var polygon = smooth.Select(p => new NormalizedPoint(result.AnchorX + p.Dx, result.AnchorY + p.Dy)).ToList();
        return result with { ShapePoints = smooth, Polygon = polygon };
    }
}
