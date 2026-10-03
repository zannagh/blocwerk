namespace Blocwerk.HoldDetection.Tiling;

/// <summary>
/// Turns per-window detections into one full-image set. A box that touches an INNER window edge (one that is
/// not the image border) is cut and dropped: the overlap guarantees a neighbouring window sees any object up
/// to the overlap size whole. Larger objects (volumes) come from a whole-image pass instead. The survivors
/// then go through a greedy, class-aware NMS plus a centre-containment dedupe for the same object detected at
/// different extents by two windows. Finally <see cref="DropTangles"/> removes texture hallucinations and
/// <see cref="DropNested"/> the zoomed-in part detections (bolt holes, features) inside a larger hold.
/// </summary>
internal static class TileBoxMerger
{
    /// <summary>Pixels from an inner window edge within which a box counts as cut.</summary>
    public const int EdgeMarginPx = 4;

    /// <summary>IoU above which the lower-confidence box of the same class is suppressed.</summary>
    public const double NmsIoU = 0.45;

    /// <summary>IoU above which a box whose centre lies inside a kept box is a duplicate.</summary>
    public const double ContainedIoU = 0.25;

    /// <summary>IoU above which two merged boxes count as tangled with each other.</summary>
    public const double TangleIoU = 0.2;

    /// <summary>A merged box tangled with at least this many others is dropped.</summary>
    public const int TangleNeighbours = 2;

    /// <summary>A box whose long side is below this fraction of a hold's long side can be a part of it.</summary>
    public const double NestedSideRatio = 0.25;

    /// <summary>...and is one when its centre lies within this fraction of the hold's half-side from its centre.</summary>
    public const double NestedCentreFraction = 0.5;

    /// <summary>Whether a window-local box touches an edge of the window that is inside the image.</summary>
    /// <param name="box">The box in window-local pixels.</param>
    /// <param name="window">The window.</param>
    /// <param name="imageWidth">Full image width.</param>
    /// <param name="imageHeight">Full image height.</param>
    /// <returns>True when the box is cut by an inner edge.</returns>
    public static bool TouchesInnerEdge(PixelBox box, TileWindow window, int imageWidth, int imageHeight)
    {
        bool left = window.X > 0 && box.Left <= EdgeMarginPx;
        bool top = window.Y > 0 && box.Top <= EdgeMarginPx;
        bool right = window.Right < imageWidth && box.Right >= window.Width - EdgeMarginPx;
        bool bottom = window.Bottom < imageHeight && box.Bottom >= window.Height - EdgeMarginPx;
        return left || top || right || bottom;
    }

    /// <summary>Maps the uncut boxes of one window to full-image pixels.</summary>
    /// <param name="local">Boxes in window-local pixels.</param>
    /// <param name="window">The window.</param>
    /// <param name="imageWidth">Full image width.</param>
    /// <param name="imageHeight">Full image height.</param>
    /// <returns>The kept boxes in full-image pixels.</returns>
    public static IEnumerable<PixelBox> ToImage(IEnumerable<PixelBox> local, TileWindow window, int imageWidth, int imageHeight) =>
        local.Where(b => !TouchesInnerEdge(b, window, imageWidth, imageHeight)).Select(b => b.Offset(window.X, window.Y));

    /// <summary>Greedy class-aware NMS by confidence, then the centre-containment dedupe.</summary>
    /// <param name="boxes">Boxes in full-image pixels, from every window.</param>
    /// <returns>The merged boxes, highest confidence first.</returns>
    public static List<PixelBox> Merge(IEnumerable<PixelBox> boxes)
    {
        var kept = new List<PixelBox>();
        foreach (var box in boxes.OrderByDescending(b => b.Confidence))
        {
            if (!kept.Exists(k => k.Label == box.Label && IsDuplicate(box, k)))
            {
                kept.Add(box);
            }
        }

        return kept;
    }

    /// <summary>
    /// Drops every box that overlaps at least <see cref="TangleNeighbours"/> other boxes (any class) at IoU above
    /// <see cref="TangleIoU"/>, measured on the square footprints the app stores (a hold is a circle of radius
    /// max(w, h) / 2), not the raw rectangles. Real holds are separate objects and survive NMS nearly disjoint; a zoomed-in
    /// window on a crash mat or a bare panel instead yields a lattice of confident, same-sized, mutually
    /// overlapping boxes. On The Attic this removed 249 of 273 off-wall boxes for 2 of 589 found holds.
    /// </summary>
    /// <param name="boxes">The merged boxes.</param>
    /// <returns>The boxes that are not part of a tangle, order preserved.</returns>
    public static List<PixelBox> DropTangles(List<PixelBox> boxes)
    {
        var squares = boxes.ConvertAll(b => b.Square());
        var kept = new List<PixelBox>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
        {
            int neighbours = 0;
            for (int j = 0; j < boxes.Count && neighbours < TangleNeighbours; j++)
            {
                if (j != i && squares[i].IoU(squares[j]) > TangleIoU)
                {
                    neighbours++;
                }
            }

            if (neighbours < TangleNeighbours)
            {
                kept.Add(boxes[i]);
            }
        }

        return kept;
    }

    /// <summary>
    /// Drops a box that is a part of a larger hold: its long side is under <see cref="NestedSideRatio"/> of a
    /// "hold" box's long side and its centre sits in the middle <see cref="NestedCentreFraction"/> of that hold.
    /// A window sees a big hold at 2-6x the whole-image scale and fires on its bolt hole or a feature; the matcher
    /// would then prefer that tiny centre over the hold. Volumes never swallow boxes: holds sit on them.
    /// </summary>
    /// <param name="boxes">The boxes.</param>
    /// <returns>The boxes that are not nested in a larger hold, order preserved.</returns>
    public static List<PixelBox> DropNested(List<PixelBox> boxes) =>
        boxes.Where(b => !boxes.Exists(h => h.Label == "hold" && IsNestedIn(b, h))).ToList();

    private static bool IsNestedIn(PixelBox part, PixelBox hold)
    {
        int holdSide = Math.Max(hold.Width, hold.Height);
        if (Math.Max(part.Width, part.Height) >= NestedSideRatio * holdSide)
        {
            return false;
        }

        double reach = NestedCentreFraction * holdSide / 2.0;
        return Math.Abs(part.CenterX - hold.CenterX) <= reach && Math.Abs(part.CenterY - hold.CenterY) <= reach;
    }

    private static bool IsDuplicate(PixelBox candidate, PixelBox kept)
    {
        double iou = candidate.IoU(kept);
        return iou > NmsIoU || (iou > ContainedIoU && kept.Contains(candidate.CenterX, candidate.CenterY));
    }
}
