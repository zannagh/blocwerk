namespace Blocwerk.HoldDetection.Tiling;

/// <summary>One detection window in full-image pixels.</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Window width (the tile size, or the image width when smaller).</param>
/// <param name="Height">Window height.</param>
internal readonly record struct TileWindow(int X, int Y, int Width, int Height)
{
    /// <summary>Gets the exclusive right edge.</summary>
    public int Right => X + Width;

    /// <summary>Gets the exclusive bottom edge.</summary>
    public int Bottom => Y + Height;
}
