namespace Blocwerk.HoldDetection.Tiling;

/// <summary>A detection box in full-image pixels (exclusive right/bottom, YoloDotNet semantics).</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
/// <param name="Confidence">Model confidence.</param>
/// <param name="Label">Class label ("hold" or "volume").</param>
internal readonly record struct PixelBox(int Left, int Top, int Right, int Bottom, double Confidence, string Label)
{
    /// <summary>Gets the box width.</summary>
    public int Width => Right - Left;

    /// <summary>Gets the box height.</summary>
    public int Height => Bottom - Top;

    /// <summary>Gets the horizontal centre.</summary>
    public double CenterX => Left + (Width / 2.0);

    /// <summary>Gets the vertical centre.</summary>
    public double CenterY => Top + (Height / 2.0);

    /// <summary>Returns the box shifted by a tile's origin.</summary>
    /// <param name="dx">Horizontal offset.</param>
    /// <param name="dy">Vertical offset.</param>
    /// <returns>The shifted box.</returns>
    public PixelBox Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy, Right = Right + dx, Bottom = Bottom + dy };

    /// <summary>The square of side max(w, h) around the same centre: the footprint of the stored hold circle.</summary>
    /// <returns>The square box.</returns>
    public PixelBox Square()
    {
        int side = Math.Max(Width, Height);
        int left = (int)Math.Round(CenterX - (side / 2.0));
        int top = (int)Math.Round(CenterY - (side / 2.0));
        return this with { Left = left, Top = top, Right = left + side, Bottom = top + side };
    }

    /// <summary>Intersection over union of two boxes.</summary>
    /// <param name="other">The other box.</param>
    /// <returns>IoU in [0, 1].</returns>
    public double IoU(PixelBox other)
    {
        long ix = Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left));
        long iy = Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
        long inter = ix * iy;
        long union = ((long)Width * Height) + ((long)other.Width * other.Height) - inter;
        return union > 0 ? (double)inter / union : 0;
    }

    /// <summary>Whether a point lies inside the box (edges inclusive).</summary>
    /// <param name="x">Point x.</param>
    /// <param name="y">Point y.</param>
    /// <returns>True when inside.</returns>
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}
