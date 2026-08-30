using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Computes the normalized wall-region rectangle used to frame the photo + hold overlay so the
/// display trims empty surroundings (floor / ceiling / walls) instead of rendering the whole
/// uncropped photo. Everything is in the shared 0..1 space that holds and the border polygon
/// already live in, so a frame derived here lines up with the overlay without any extra math.
/// </summary>
public static class WallFraming
{
    // Breathing room added around the raw region so holds on the very edge are not clipped.
    private const double Padding = 0.05;

    // A region thinner than this in either axis is treated as degenerate (no usable frame).
    private const double MinSpan = 0.05;

    // When the padded region already covers this much of both axes, framing would gain nothing,
    // so we skip it and render exactly as before (full photo).
    private const double NearFull = 0.9;

    /// <summary>
    /// The normalized [minX, minY, maxX, maxY] wall region to frame, taken from the border polygon
    /// when it is present and non-degenerate, otherwise from the bounding box of the holds. Returns
    /// null when there is nothing to frame or the region is essentially the whole image — callers
    /// then leave the view uncropped.
    /// </summary>
    public static double[]? TryComputeFrame(
        IReadOnlyList<ShapePoint>? border,
        IReadOnlyList<Hold>? holds)
    {
        double minX, minY, maxX, maxY;

        if (border is { Count: >= 3 })
        {
            minX = border.Min(p => p.Dx);
            maxX = border.Max(p => p.Dx);
            minY = border.Min(p => p.Dy);
            maxY = border.Max(p => p.Dy);
        }
        else if (holds is { Count: > 0 })
        {
            minX = holds.Min(h => h.X - h.Radius);
            maxX = holds.Max(h => h.X + h.Radius);
            minY = holds.Min(h => h.Y - h.Radius);
            maxY = holds.Max(h => h.Y + h.Radius);
        }
        else
        {
            return null;
        }

        if (maxX - minX < MinSpan || maxY - minY < MinSpan)
        {
            return null;
        }

        minX = Math.Clamp(minX - Padding, 0, 1);
        minY = Math.Clamp(minY - Padding, 0, 1);
        maxX = Math.Clamp(maxX + Padding, 0, 1);
        maxY = Math.Clamp(maxY + Padding, 0, 1);

        if (maxX - minX >= NearFull && maxY - minY >= NearFull)
        {
            return null;
        }

        return [minX, minY, maxX, maxY];
    }
}
