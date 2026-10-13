// <copyright file="SegmentWindows.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.HoldDetection.Segmentation;

/// <summary>A hold's detector box in full-image pixels.</summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
internal readonly record struct SegmentBox(double Left, double Top, double Right, double Bottom)
{
    /// <summary>Gets the longer side.</summary>
    public double LongSide => Math.Max(Right - Left, Bottom - Top);
}

/// <summary>One square window of the photo the segmenter looks at once, and the holds it outlines in it.</summary>
/// <param name="X">Left edge in full-image pixels.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width (the side, or less where the image is smaller).</param>
/// <param name="Height">Height.</param>
/// <param name="Members">Indexes of the holds outlined in this window.</param>
internal sealed record SegmentWindow(int X, int Y, int Width, int Height, IReadOnlyList<int> Members);

/// <summary>
/// Groups holds into windows so the costly half of the segmenter (one pass per window) is shared: a window is sized
/// to its holds (<see cref="Span"/> times the long side of the hold it is built around), and takes every other hold
/// of a similar size that lies in it with room to spare. A hold is therefore always seen at a scale where it covers
/// a fair part of the window, whether it is a foot chip or a volume. Pure, and the same boxes always give the same
/// windows (largest hold first, ties by index).
/// </summary>
internal static class SegmentWindows
{
    /// <summary>Window side in long sides of the hold the window is built around.</summary>
    public const double Span = 6;

    /// <summary>Smallest window side in pixels.</summary>
    public const int MinSide = 256;

    /// <summary>A hold joins a window when its long side is at least this share of the window's own hold.</summary>
    public const double MinRelativeSize = 0.4;

    /// <summary>...and at most this many times it.</summary>
    public const double MaxRelativeSize = 1.6;

    /// <summary>Room a joining hold needs to every window edge, in its own long sides.</summary>
    public const double Margin = 0.5;

    /// <summary>Plans the windows for the boxes of one photo.</summary>
    /// <param name="boxes">The boxes.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <returns>The windows; every box index is a member of exactly one.</returns>
    public static List<SegmentWindow> Plan(IReadOnlyList<SegmentBox> boxes, int width, int height)
    {
        var order = Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].LongSide).ThenBy(i => i).ToList();
        var left = new SortedSet<int>(order);
        var windows = new List<SegmentWindow>();
        foreach (int i in order)
        {
            if (!left.Contains(i))
            {
                continue;
            }

            var (x, y, w, h) = Around(boxes[i], width, height);
            var members = new List<int> { i };
            left.Remove(i);
            foreach (int j in left.ToList())
            {
                if (Fits(boxes[j], boxes[i].LongSide, x, y, w, h))
                {
                    members.Add(j);
                    left.Remove(j);
                }
            }

            windows.Add(new SegmentWindow(x, y, w, h, members));
        }

        return windows;
    }

    /// <summary>The window built around one box: square where the image allows, shifted to stay inside it.</summary>
    /// <param name="box">The box.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <returns>The window rectangle.</returns>
    public static (int X, int Y, int Width, int Height) Around(SegmentBox box, int width, int height)
    {
        double side = Math.Max(MinSide, Span * box.LongSide);
        int w = (int)Math.Min(width, Math.Ceiling(side));
        int h = (int)Math.Min(height, Math.Ceiling(side));
        double cx = (box.Left + box.Right) / 2, cy = (box.Top + box.Bottom) / 2;
        int x = (int)Math.Clamp(Math.Round(cx - (w / 2.0)), 0, width - w);
        int y = (int)Math.Clamp(Math.Round(cy - (h / 2.0)), 0, height - h);
        return (x, y, w, h);
    }

    private static bool Fits(SegmentBox box, double ownerSide, int x, int y, int w, int h)
    {
        double side = box.LongSide;
        if (side < MinRelativeSize * ownerSide || side > MaxRelativeSize * ownerSide)
        {
            return false;
        }

        double room = Margin * side;
        return box.Left - room >= x && box.Top - room >= y && box.Right + room <= x + w && box.Bottom + room <= y + h;
    }
}
