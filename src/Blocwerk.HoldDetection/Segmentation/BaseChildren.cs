// <copyright file="BaseChildren.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.HoldDetection.Segmentation;

/// <summary>
/// Which detector boxes lie on another box as holds screwed onto a large hold: at least <see cref="MinInside"/> of the
/// smaller box lies inside the larger one (a hold on the base's tip sticks out of the base's box) and it is at most
/// <see cref="MaxAreaShare"/> of its area. Pure.
/// </summary>
internal static class BaseChildren
{
    /// <summary>A hold on a base has at most this share of the base box's area.</summary>
    public const double MaxAreaShare = 1.0 / 3;

    /// <summary>A base's box is at least this many times the median box of the photo: a large hold, not a neighbour.</summary>
    public const double MinBaseFactor = 4;

    /// <summary>...and at least this share of it lies inside the base's box.</summary>
    public const double MinInside = 0.3;

    /// <summary>The boxes lying on the given one.</summary>
    /// <param name="boxes">Every box of the photo.</param>
    /// <param name="index">The box that may be a base.</param>
    /// <returns>The indexes of the boxes on it; empty when it carries none.</returns>
    public static List<int> Of(IReadOnlyList<SegmentBox> boxes, int index)
    {
        var b = boxes[index];
        double area = (b.Right - b.Left) * (b.Bottom - b.Top);
        var areas = boxes.Select(x => (x.Right - x.Left) * (x.Bottom - x.Top)).Order().ToList();
        if (areas.Count == 0 || area < MinBaseFactor * areas[areas.Count / 2])
        {
            return [];
        }

        return [.. Enumerable.Range(0, boxes.Count).Where(i => i != index && boxes[i] is var c
            && (c.Right - c.Left) * (c.Bottom - c.Top) is var own && own > 0 && own <= MaxAreaShare * area
            && Math.Max(0, Math.Min(c.Right, b.Right) - Math.Max(c.Left, b.Left)) * Math.Max(0, Math.Min(c.Bottom, b.Bottom) - Math.Max(c.Top, b.Top)) >= MinInside * own)];
    }
}
