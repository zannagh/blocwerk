using Blocwerk.Core.Holds;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Colours for the segment overlays. Segments are allowed to overlap and the first one in
/// <c>SortOrder</c> wins, so every segment needs a colour of its own that survives being
/// drawn translucently on top of a wall photo as well as on the pale schematic.
/// </summary>
/// <remarks>
/// Deliberately avoids the orange <c>#FF9800</c> of the wall border, the lavender
/// <c>#c86dff</c> of virtual holds and the start/top green/red of boulder holds.
/// </remarks>
public static class WallSegmentPalette
{
    private static readonly string[] Hues =
    [
        "#00bcd4", // cyan
        "#ff5722", // deep orange
        "#8bc34a", // lime
        "#e91e63", // pink
        "#3f51b5", // indigo
        "#ffc107", // amber
        "#009688", // teal
        "#9c27b0", // purple
    ];

    /// <summary>The opaque outline colour of the segment at the given position.</summary>
    public static string Stroke(int index) => Hues[((index % Hues.Length) + Hues.Length) % Hues.Length];

    /// <summary>The translucent body colour of the segment at the given position.</summary>
    public static string Fill(int index, double alpha = 0.18) => HoldPalette.Rgba(Stroke(index), alpha);
}
