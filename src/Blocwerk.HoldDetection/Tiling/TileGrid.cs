namespace Blocwerk.HoldDetection.Tiling;

/// <summary>
/// Lays overlapping square windows over an image: starts advance by <c>tile - overlap</c>, and the last
/// window on each axis is pinned flush to the far edge so the grid always covers the whole image (the last
/// overlap can therefore be larger than configured). An image smaller than a tile gets one window.
/// </summary>
internal static class TileGrid
{
    /// <summary>Computes the windows covering an image, row-major.</summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="tile">Window edge in pixels.</param>
    /// <param name="overlap">Overlap between neighbours in pixels (must be less than <paramref name="tile"/>).</param>
    /// <returns>The windows.</returns>
    public static IReadOnlyList<TileWindow> Compute(int width, int height, int tile, int overlap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tile, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlap, tile);

        var xs = Starts(width, tile, overlap);
        var ys = Starts(height, tile, overlap);
        var windows = new List<TileWindow>(xs.Count * ys.Count);
        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                windows.Add(new TileWindow(x, y, Math.Min(tile, width - x), Math.Min(tile, height - y)));
            }
        }

        return windows;
    }

    /// <summary>Window start offsets along one axis.</summary>
    /// <param name="size">Axis length.</param>
    /// <param name="tile">Window edge.</param>
    /// <param name="overlap">Overlap.</param>
    /// <returns>Ascending, distinct start offsets.</returns>
    internal static List<int> Starts(int size, int tile, int overlap)
    {
        var starts = new List<int> { 0 };
        if (size <= tile)
        {
            return starts;
        }

        int step = tile - overlap;
        int last = size - tile;
        for (int s = step; s < last; s += step)
        {
            starts.Add(s);
        }

        starts.Add(last);
        return starts;
    }
}
